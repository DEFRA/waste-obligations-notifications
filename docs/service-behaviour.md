# Service behaviour

This document describes message contracts and processing requirements. Use
[CONTEXT.md](../CONTEXT.md) for terminology and the linked ADRs for decision
rationale. The command architecture is accepted; the cutover ADR remains proposed. Current
implementation scope is described below.

## Current scope

The analytics consumer logs event and entity IDs and deletes successfully
processed messages. It does not deliver notifications or persist event data.
The command consumer records pre-cutover commands as `delivery-suppressed` and
sends at-or-after-cutover commands through GOV.UK Notify under a Mongo claim.
Notify acceptance must be recorded before SQS deletion. Matching accepted,
suppressed, or abandoned records suppress duplicates regardless of the current
cutover. Analytics does not yet create notification commands, and there is no
administrator DLQ management surface. Redrive does not restore a command's
original recipient-lane position.

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
- Validate the UTC cutover and configured evidence and recipient-lane secrets
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
  and unique notification-key index before receiving commands from SQS or
  persisting them. Completion by another host can satisfy this check, including
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
  New suppression records retain their original eight fields; optional lease
  and Notify fields are omitted when absent. Claims and accepted records keep
  their additional evidence. Existing documents are not rewritten, and older
  record models are not expected to read pending or accepted records.
- Treat a duplicate command with different immutable fields as a conflict and
  leave it retryable. A pre-cutover command is terminal only after its
  `delivery-suppressed` outcome is recorded. A nonterminal claim cannot be
  mistaken for suppressed evidence or overwritten by suppression.
- Claim a post-cutover command atomically using the unique notification-key
  index, matching immutable digest, fresh attempt owner, and Mongo's expiry
  clock. Active claims cannot send again; expired claims permit one new owner.
  Acceptance updates require the same owner and an unexpired lease using Mongo's
  clock. Accepted evidence includes the opaque versioned HMAC Notify reference,
  template ID/version, Notify notification ID, correlation digests, and timestamps.
- Make one Notify request per claim with no HTTP retry or redirect. Normalize the
  recipient for the request. Require `201 Created` and consistent minimal
  acceptance evidence; malformed success is indeterminate and remains retryable.
  Never log dependency exception text or full responses, which may contain PII.
- Validate the complete bounded attempt budget at startup. Initially visibility
  and command leases are 120 seconds; receive, claim, send, acceptance and deletion
  bounds are 30, 5, 60, 10 and 5 seconds, plus 10 seconds headroom. Measure elapsed
  time monotonically from receive/claim request starts and reject late confirmations
  before sending. Request visibility explicitly on receive without changing shared
  queue configuration. Pass cancellation through the entire Notify request and
  response buffering. A failed send retains its claim until expiry.
- A Notify timeout, lost response, crash, or failed acceptance write does not prove
  rejection. Queue retry after expiry may send a duplicate email. An already
  in-flight request can outlive ownership during a process stall; Mongo rejects
  stale acceptance. No Notify-reference reconciliation is implemented. Queue
  deletion failure after durable acceptance retries as a terminal duplicate.
- Compare the immutable UTC business-action timestamp with the deployment-owned
  cutover value. Both configured cutover and serialized action timestamps must
  explicitly include `Z` or a zero offset (`+00:00` or `-00:00`). Reject absent
  or nonzero offsets instead of interpreting them in the host timezone or
  converting them. Truncate both timestamps to whole milliseconds before
  comparison, matching MongoDB storage precision. Apply the same truncation to
  command serialisation and immutable-field digests so sub-millisecond precision
  lost on a Mongo roundtrip does not create a conflict. Do not use processing time
  or mutable entity state.

Operational logs and metrics use a diagnostics-only allowlist of notification
categories, with an `other` fallback for unknown values. Startup bounds the list
to 32 non-PII labels of at most 64 lowercase ASCII letters, digits or hyphens.
This does not restrict producer-defined command types or alter stored immutable
identity. Claim results and durations, Notify accepted/failed attempts and send
durations, and terminal duplicates are observed at the hosted-consumer boundary.
Notify acceptance is counted before persistence; failure means an attempt lacked
confirmed acceptance and includes indeterminate cancellation and timeout. These
metrics do not claim recipient delivery or rejection.

Instruments follow Waste Obligations' DI-owned meter and singleton instrumentation
conventions. Shared names and tag keys use PascalCase; counters use CloudWatch
`COUNT` and claim/send durations use `MILLISECONDS`, matching email-send timing.
The only dimensions are the fixed Notifications `Service`, bounded
`NotificationType` and fixed `Outcome`. A DI-owned CloudWatch EMF exporter starts
before the command consumer and observes only its host's meter. It uses the Waste
Obligations SDK/configuration mechanism without shared SDK configuration or
platform-property decoration. One measurement emits one EMF document; optional
agent log-group/stream routing is preserved outside metric dimensions.

Export defaults to enabled and requires a configured namespace, except that
`AWS_EMF_ENVIRONMENT=Local` allows a blank namespace and uses the Notifications
namespace. Disabled export does not resolve an SDK environment. Local development
and isolated tests disable it. Unknown-environment discovery uses bounded,
cancellable startup metadata requests; delivery never fetches metadata. Export
failures and full-buffer drops yield fixed sanitized diagnostics and do not affect
command processing. Observation stops before sink shutdown; the host bounds its
wait, but the SDK's background worker has no cancellation API and may outlive that
wait. Metrics are best effort. See the README for configuration and CDP routing.

With command processing enabled, `/health/all` includes a light read-only Notify
connectivity check. It makes one authenticated `GET /v2/templates?type=email`,
requires `200 OK`, and disposes the response without reading template content.
The existing ten-second health timeout cancels the HTTP request; a late response
after cancellation cannot report healthy. Results and logs expose only fixed
descriptions, without dependency exceptions or response data. Disabled command
processing does not register or call Notify health. `/health` stays independent
of extended dependency checks. This check does not validate a specific template
or confirm recipient delivery.

The command architecture is described in
[ADR 0001](adr/0001-notification-command-delivery-architecture.md), and the
cutover decision in [ADR 0002](adr/0002-email-delivery-cutover-boundary.md).

## Deployment ownership

- The analytics queue must be a separate SNS subscription from the producer
  queue, with its own redrive policy. Never reuse the producer queue.
- `AnalyticsEventConsumer__QueueUrl` and
  `AnalyticsEventConsumer__ProcessingEnabled` are deployment-owned settings.
- Command queue, MongoDB, cutover, digest-secret and `AWS_EMF_*` settings are also
  deployment-owned. Local Compose settings do not configure CDP environments.
  Set the actual collector endpoint explicitly for CDP/FluentBit; the pinned SDK's
  Fluent-host endpoint derivation is malformed.

## Behaviour verification

Unit tests cover parsing, validation, logging, deletion, and failure behaviour.
Compose-backed integration tests cover health and SNS-to-SQS-to-consumer wiring.
Test that stored command evidence and logs do not disclose protected identifiers,
addresses, templates, or personalisation. Analytics event and entity IDs are
logged as required by the analytics contract above.
