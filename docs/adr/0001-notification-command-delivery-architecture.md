# ADR 0001: Notification-command delivery architecture

**Status:** accepted

**Date:** 2026-09-29

## Context

Notifications currently consumes raw analytics events and only logs their IDs.
MO-561 needs a reusable email-delivery capability for analytics-driven and
scheduled notification producers. It must avoid duplicate sends across SQS
redelivery and multiple hosts, retain minimal evidence, and preserve
per-recipient command order.

Sending directly from each producer would duplicate delivery policy and make
cross-host duplicate handling inconsistent. Using SQS FIFO deduplication alone
would not protect an intended email after its limited deduplication window.

## Decision

Notifications will consume versioned notification commands from a dedicated,
service-owned SQS FIFO queue. Upstream producers use a shared command publisher
to assign the command's idempotency key as the SQS deduplication ID and a
non-reversible recipient lane as the message-group ID.

The delivery service will use MongoDB notification records with a unique
notification-key index and a per-notification lease. The record provides durable
duplicate suppression and coordinates competing hosts. The handler makes one
GOV.UK Notify send request for a claimed command; a Notify error or an inability
to record Notify acceptance leaves the SQS message for visibility-timeout
redelivery and the queue redrive policy.

This decision covers source-independent command delivery. Producer-specific
recipient/template policy and administrator DLQ operations are separate scopes.

Each claim has a fresh attempt owner. MongoDB's current time determines expiry,
and recording acceptance requires that exact owner and a pending claim. Expiry
alone does not reject confirmed acceptance; replacing the owner or recording a
terminal outcome does. A
bounded attempt uses conservative monotonic deadlines from receive and claim
request starts; startup validates that dependency timeouts and headroom fit the
command lease and SQS visibility. Late confirmations do not authorise a send.
The default command lease is 90 seconds against 120-second SQS visibility,
leaving headroom for prompt retry after expiry. Failed or indeterminate requests
retain the claim until expiry rather than
releasing it early. Delivery claims are separate from migration leases.

A `201 Created` response means accepted by Notify. Once a complete valid response
arrives, attempt persistence with a fresh bounded token, including after send
timeout, lease expiry or shutdown. Timing checks guard new sends, not known
acceptance. Recording valid acceptance precedes queue deletion; matching accepted, suppressed or abandoned records
prevent another send. A crash, lost response, timeout or failed acceptance write
can leave an indeterminate send. Retry after expiry may send the email again;
Notify-reference reconciliation is excluded. Mongo fences acceptance writes,
but cannot fence an HTTP request already in flight during a process stall.

Minimal acceptance evidence consists of command/recipient/immutable-field HMAC
digests, a versioned HMAC Notify reference, template ID/version, Notify
notification ID, timestamps and outcome. Command immutable identity is preserved
exactly, including template spelling; a canonical UUID in Notify's response can
identify the same requested template. Diagnostics use a bounded configured
category allowlist with a fixed fallback, preserving arbitrary producer-defined
command types without exposing their raw values in logs or metric dimensions.

Delivery metrics use Waste Obligations' DI-managed instruments and CloudWatch
EMF mechanism. Each host owns its configuration, SDK environment and observer;
only bounded command dimensions are exported, with no SDK platform decoration.
Metric failures do not alter delivery. Observation stops before a bounded sink
shutdown wait; the SDK worker can outlive that wait because it has no cancellation
API.

## Consequences

The delivery service is source-independent: it does not consume a raw analytics
event as its email-delivery contract and does not choose recipients, templates,
or notification policy. Analytics-driven and scheduled components publish the
same command format.

SQS FIFO ordering is limited to the recipient lane. A failed message can hold
later commands in that lane until it reaches the DLQ, and a redriven message can
be sent after later commands.

MongoDB, the command queue and DLQ, Notify credentials, and identity-key
material become deployment-owned dependencies. Records retain minimal delivery
evidence indefinitely and must not retain recipient addresses, personalisation,
rendered content, or full Notify responses.
