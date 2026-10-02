# Service behaviour

This document describes message contracts and processing requirements. Use
[CONTEXT.md](../CONTEXT.md) for terminology and the linked ADRs for decision
rationale. The command architecture remains proposed in this initial slice; the cutover
ADR is accepted. Current scope below does not include Notify sending.

## Current scope

The analytics consumer logs event and entity IDs and deletes successfully
processed messages. It does not deliver notifications or persist event data.
With a null cutover, the command consumer records all valid commands as
`delivery-suppressed` and deletes them. With a configured boundary it records
pre-cutover commands as `delivery-suppressed` and
deletes them without sending to GOV.UK Notify. Post-cutover delivery belongs to
ticket 02. Until then, commands at or after the boundary fail without deletion,
retry after visibility timeout, and can reach the DLQ under queue redrive policy.
Operators may need to redrive them once delivery is enabled. Redrive does not
restore a command's original recipient-lane position.

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
- Background consumers should long-poll, process the configured batch, and log
  non-cancellation exceptions. Analytics consumption waits for its configured
  poll interval after each receive. Command consumption immediately receives
  again after success or an empty response; its poll interval is backoff only
  after errors. The command receive timeout must exceed its long-poll wait and
  bounds that receive without changing the shared SQS client's configuration.
  A disabled consumer logs once and awaits cancellation.
- Apply the logging restrictions in [coding standards](../CODING_STANDARDS.md).

## Notification commands and data protection

- Keep notification delivery separate from the analytics-event path. Analytics
  consumption does not deliver notifications, mutate business data, transform
  payloads, or persist event data.
- Validate commands before publishing or consuming them. Normalise a recipient
  only where the command contract requires it.
- Allow an unset cutover; validate any supplied UTC cutover and configured evidence and recipient-lane secrets
  at startup when command processing is enabled. Invalid configuration must
  not consume commands; disabled processing permits deployment placeholders.
  Digest creation also rejects unconfigured secrets independently of processing.
- Use the idempotency key as the FIFO message-deduplication ID and a
  non-reversible per-recipient digest as the FIFO message-group ID.
  Validate the key before publishing or consuming: it must contain 1–128
  characters from the ASCII letters, digits and punctuation allowed by
  [SQS SendMessage](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessage.html#API_SendMessage_RequestParameters).
  Reject invalid keys without changing them; never trim, truncate or replace
  the key to fit the queue constraints.
- Run versioned Mongo migrations under a renewable exclusive lease when command
  processing is enabled. Each host must verify the required migration version
  and latest migration's current schema before receiving commands from SQS.
  Migration 001 validates its unique notification-key index; future migrations
  explicitly define their own current-schema requirements. Migration completion
  is a one-time startup signal. The consumer waits before its first receive,
  while record stores have no completion dependency or per-operation wait.
  This does not detect later manual schema changes. Completion by another host can satisfy this check, including
  after the local host exhausts its migration attempts. A lease-renewal failure
  cancels the engine; only after it stops can the host release and reacquire
  the lease. Attempts share a bounded host-wide budget across acquisitions.
  Host shutdown cancels the migration engine but continues renewing its lease
  while renewal succeeds until execution stops.
  Bound acquisition and renewal confirmation by a deadline measured from the
  request start, reserving half a renewal interval before expiry. Cancel the
  engine independently of renewal I/O when confirmation misses that deadline;
  ignore late success and reject renewal of an expired lease. Await outstanding
  renewal work before local release or reacquisition. Cancellation-resistant
  engine operations can still outlive ownership loss; the lease provides no
  fencing after expiry.
  Migration failures and
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
  cutover value. Any supplied cutover and serialized action timestamps must
  explicitly include `Z` or a zero offset (`+00:00` or `-00:00`). Reject absent
  or nonzero offsets instead of interpreting them in the host timezone or
  converting them. Truncate both timestamps to whole milliseconds before
  comparison, matching MongoDB storage precision. Apply the same truncation to
  command serialisation and immutable-field digests so sub-millisecond precision
  lost on a Mongo roundtrip does not create a conflict. Do not use processing time
  or mutable entity state.

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

## Initial suppression mode

The default cutover is null. Enabled processing suppresses all valid commands
without a Notify send while Waste Obligations retains direct delivery. Suppression
must be durable before deletion; malformed commands, conflicts and persistence
failures retain the message. Existing suppressed evidence is terminal when a
future cutover is configured. A non-null cutover requires explicit UTC and the
same millisecond precision as command identity. Other configuration requirements
remain in force.

Use the producer dry run before choosing the identical future X in both services.
Verify both deployments complete before X; after X use Notifications recovery
and do not clear or move the cutover backward. ADR0002 records this accepted,
forward-only handover. Local examples do not configure deployed values.

Migration completion is reported by the `MongoMigrationCompletion` entry in
`/health/all` while command processing is enabled. Until completion that endpoint
returns 503, while `/health` and analytics remain available. If every host
exhausts its migration attempts, repair the migration problem and restart a host
to retry; hosts continue observing completion by a peer meanwhile. This check
reads the startup signal and performs no schema or index queries.
