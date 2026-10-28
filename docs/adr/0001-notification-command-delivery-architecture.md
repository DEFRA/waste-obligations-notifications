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

The [producer contract](../notification-command-producer-contract.md) pins the
wire and FIFO rules; it does not prescribe declaration recipient/template policy.

Notifications will consume versioned notification commands from a dedicated,
service-owned SQS FIFO queue. Upstream producers use a shared command publisher
to assign the command's idempotency key as the SQS deduplication ID and a
non-reversible recipient lane as the message-group ID.

Trace correlation travels as an optional JSON field, following analytics events.
A command retains its originating trace without coupling diagnostic context to
the immutable email intention. Messages without correlation remain valid; the
[producer contract](../notification-command-producer-contract.md#trace-correlation)
defines the transport convention.

The delivery service will use MongoDB notification records with a unique
notification-key index and a per-notification lease. The record provides durable
duplicate suppression and coordinates competing hosts. The handler makes one
GOV.UK Notify send request for a claimed command; a Notify error or an inability
to record Notify acceptance leaves the SQS message for visibility-timeout
redelivery and the queue redrive policy.

This decision covers source-independent command delivery. Producer-specific
recipient/template policy and administrator DLQ operations are separate scopes.

Claim ownership, monotonic deadlines and startup budget validation are implemented
in the [delivery consumer](../../src/Consumer/Delivery/NotificationCommandConsumer.cs),
[store](../../src/Consumer/Delivery/MongoNotificationDeliveryRecordStore.cs) and
[options](../../src/Consumer/Delivery/NotificationCommandDeliveryOptions.cs).
Mongo coordinates attempts but cannot fence an HTTP request already in flight.
Delivery claims are separate from migration leases.

A `201 Created` response means accepted by Notify. Once a complete valid response
arrives, attempt persistence with a fresh bounded token, including after send
timeout, lease expiry or shutdown. Timing checks guard new sends, not known
acceptance. Recording valid acceptance precedes queue deletion; matching accepted, suppressed or abandoned records
prevent another send. A crash, lost response, timeout or failed acceptance write
can leave an indeterminate send. Retry after expiry may send the email again;
Notify-reference reconciliation is excluded. Mongo fences acceptance writes,
but cannot fence an HTTP request already in flight during a process stall.

Retain only minimal correlation and outcome evidence, with a bounded diagnostic
category allowlist so producer-defined types do not leak into logs or metrics.
The [evidence model](../../src/Consumer/Delivery/NotificationDeliveryEvidence.cs),
[digest](../../src/Consumer/Delivery/NotificationCommandDigest.cs) and
[metrics](../../src/Consumer/Delivery/NotificationCommandMetrics.cs) define the
current fields and instruments. Metrics use the companion service's DI/CloudWatch
EMF conventions and remain best effort; export failures cannot alter delivery.

## Consequences

The [submitted producer](../../src/Consumer/Submission/SubmittedEmailProducer.cs)
uses declaration identity rather than analytics-event identity for replay. The
[cancellation producer](../../src/Consumer/Cancellation/CancellationEmailProducer.cs)
shares original-submission construction and confirms that predecessor before
cancellation publications when recipients exist. This adds no producer ledger or
alternate send mechanism; live contacts are re-evaluated on retry. See
[service entry points](../service-behaviour.md#cancellation-production) and tests
for exact selection, mapping and empty-result behaviour.

Event snapshots keep recorded notification intentions independent of a declaration’s
continued existence, without introducing archival storage or historical API
endpoints. We use a declaration API 404 as the missing-record signal, accepting that
it cannot distinguish deletion from routing or configuration errors: the validated
analytics snapshot is trusted evidence of the recorded action. The
[shared reader](../../src/Consumer/Submission/SubmittedEmailProducer.cs)
and [deletion/replay scenarios](../../tests/Consumer.IntegrationTests/Scenarios/CancellationEmailSendTests.cs)
define the implemented boundary.

This shared prefix establishes first-publication precedence on every host, without
a global sequencer or ordering across declarations. Publication confirmation does
not inspect Mongo immutable conflicts or prove Notify acceptance. Different
recipient lanes, DLQ recovery and resend can invert later delivery and inbox order;
the existing indeterminate Notify duplicate risk remains. Shared cutover activation
still requires both producers and verified deployed access/configuration.

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

## Source correlation for delivery evidence

Commands may carry a non-sensitive, generic source type and ID. These are retained with delivery evidence for authenticated search and participate in immutable-command conflict detection. They do not define delivery identity: one source can produce several emails, and source-independent producers may omit them. Recipient search uses the existing keyed digest, without retaining an address in delivery evidence.

Introducing source metadata changes the immutable digest for commands that supply it. Cancellation also changes its key prefix. Existing records are not migrated: deployment must account for commands crossing this boundary, since replays can conflict with earlier submission evidence or create a new cancellation identity.

Valid correlation IDs may be retained as optional diagnostic evidence and searched. Retain the first value written on creation, claim or abandonment; retries do not replace it. Correlation remains outside immutable command identity and terminal duplicate handling does not update it.
