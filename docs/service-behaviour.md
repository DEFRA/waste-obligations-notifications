# Service behaviour

This document describes message contracts and processing requirements. Use
[CONTEXT.md](../CONTEXT.md) for terminology and the linked ADRs for decision
rationale. Both ADRs are currently proposed; their planned delivery behaviour
must not be read as a claim that sending is implemented.

## Current scope

The analytics consumer logs event and entity IDs and deletes successfully
processed messages. It does not deliver notifications or persist event data.
The command consumer records pre-cutover commands as `delivery-suppressed` and
deletes them without sending to GOV.UK Notify. Post-cutover delivery belongs to
the next implementation ticket.

## Consumers and message handling

- Validate a message at the boundary, before any terminal action. Reject
  malformed JSON, missing required values, invalid command shape, invalid UTC
  timestamps, unsupported schema versions, and unsupported encodings.
- Preserve the analytics-event transport contract: raw JSON and
  `Content-Encoding: gzip+base64` are supported. Any other declared encoding is
  a processing failure.
- Do not filter analytics events by event type or entity type. Log a parsed
  event's `eventId` and `entityId` before deleting its SQS message.
- Delete an SQS message only after all work needed to make it terminal has
  succeeded. Parsing, validation, persistence, or processing failures must
  leave the message in the queue for its redrive policy.
- Background consumers should long-poll, process the configured batch, log
  non-cancellation exceptions, and wait for their configured poll interval.
  A disabled consumer logs once and awaits cancellation.
- Apply the logging restrictions in [coding standards](../CODING_STANDARDS.md).

## Notification commands and data protection

- Keep notification delivery separate from the analytics-event path. Analytics
  consumption does not deliver notifications, mutate business data, transform
  payloads, or persist event data.
- Validate commands before publishing or consuming them. Normalise a recipient
  only where the command contract requires it.
- Use the idempotency key as the FIFO message-deduplication ID and a
  non-reversible per-recipient digest as the FIFO message-group ID.
- Run versioned Mongo migrations under a renewable exclusive lease when command
  processing is enabled. Each host must verify the required migration version
  and unique notification-key index before receiving commands from SQS or
  persisting them. Completion by another host can satisfy this check, including
  after the local host exhausts its migration attempts. Migration failures and
  prolonged readiness waits produce error logs; `/health` and analytics
  consumption remain independent of migration readiness.
- Persist only the minimal, versioned HMAC evidence needed for command
  idempotency and outcomes. Do not persist recipient addresses,
  personalisation, template content, rendered content, or full GOV.UK Notify
  responses.
- Treat a duplicate command with different immutable fields as a conflict and
  leave it retryable. A pre-cutover command is terminal only after its
  `delivery-suppressed` outcome is recorded.
- Compare the immutable UTC business-action timestamp with the deployment-owned
  cutover value. Do not use processing time or mutable entity state.

The command architecture is described in
[ADR 0001](adr/0001-notification-command-delivery-architecture.md), and the
cutover decision in [ADR 0002](adr/0002-email-delivery-cutover-boundary.md).

## Deployment ownership

- The analytics queue must be a separate SNS subscription from the producer
  queue, with its own redrive policy. Never reuse the producer queue.
- `AnalyticsEventConsumer__QueueUrl` and
  `AnalyticsEventConsumer__ProcessingEnabled` are deployment-owned settings.
- Command queue, MongoDB, cutover, and digest-secret settings are also
  deployment-owned. Local Compose settings do not configure CDP environments.

## Behaviour verification

Unit tests cover parsing, validation, logging, deletion, and failure behaviour.
Compose-backed integration tests cover health and SNS-to-SQS-to-consumer wiring.
Test that stored command evidence and logs do not disclose protected identifiers,
addresses, templates, or personalisation. Analytics event and entity IDs are
logged as required by the analytics contract above.
