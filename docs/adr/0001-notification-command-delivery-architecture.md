# ADR 0001: Notification-command delivery architecture

**Status:** proposed

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

The [producer contract](../notification-command-producer-contract.md) pins the
wire and FIFO rules; it does not prescribe declaration recipient/template policy.

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

Use MongoDB server time for suppression timestamps so host-clock skew does not
alter evidence chronology.

The evidence digest secret is part of durable command identity. Preserve it for
the lifetime of retained evidence and replayable commands; replacing it can
bypass duplicate suppression. The digest format version is not a key ID, and
rotation is unsupported without an identity-preserving migration. The recipient
lane secret must match every publisher and consumer; changing it while commands
remain queued or dead-lettered can break per-recipient ordering.
