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
cutover. Analytics does not yet create notification commands. Configured Basic
administrators can inspect, redrive or discard one command-DLQ message. Redrive does not restore a command's
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
  at startup when command processing is enabled. Digest secrets are also required
  for enabled command-DLQ administration; cutover and sending budgets are required
  only for sending. Invalid configuration must not consume commands; disabled
  capabilities permit deployment placeholders.
  Digest creation also rejects unconfigured secrets independently of processing.
- Use the idempotency key as the FIFO message-deduplication ID and a
  non-reversible per-recipient digest as the FIFO message-group ID.
  Validate the key before publishing or consuming: it must contain 1–128
  characters from the ASCII letters, digits and punctuation allowed by
  [SQS SendMessage](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessage.html#API_SendMessage_RequestParameters).
  Reject invalid keys without changing them; never trim, truncate or replace
  the key to fit the queue constraints.
- Run versioned Mongo migrations under a renewable exclusive lease when command
  processing or command-DLQ administration is enabled. Each host must verify the required migration version
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
  The pinned `GovukNotify` client owns email authentication, serialization and
  response models, using Waste Obligations' injectable client factory convention.
  Its per-operation transport preserves configured routing and carries the
  operation's cancellation through complete response buffering. SDK request and
  response objects are disposed after success or failure without disposing the
  shared typed `HttpClient`. JSON personalisation values retain their original
  semantics; only minimal acceptance evidence is projected from the SDK model.
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
requires `200 OK`, and uses the pinned SDK template-list operation. The SDK
reads and deserializes template data; it is discarded without logging, persistence
or exposure in the health result. The existing ten-second health timeout cancels
the complete HTTP request and response buffering; a late response
after cancellation cannot report healthy. Results and logs expose only fixed
descriptions, without dependency exceptions or response data. Disabled command
processing does not register or call Notify health. `/health` stays independent
of extended dependency checks. This check does not validate a specific template
or confirm recipient delivery.

The command architecture is described in
[ADR 0001](adr/0001-notification-command-delivery-architecture.md), and the
cutover decision in [ADR 0002](adr/0002-email-delivery-cutover-boundary.md).

## Command-DLQ inspection

Administration is disabled by default and exposes no routes while disabled.
Enabled `POST /admin/notification-commands/dlq/inspect` requires authenticated
Basic credentials from the Waste Obligations `Acl.Clients` shape with an `admin`
scope. Unknown clients, OAuth/Bearer callers, incorrect or malformed credentials
and read/write-only clients cannot reach queue or record operations. Validate
enabled ACL entries, admin credentials, distinct command/DLQ FIFO URLs and digest secrets at
startup. See [ADR 0004](adr/0004-command-dlq-inspection.md).

Administration can run while sending is paused. It starts the same Mongo
migrations and waits for verified readiness before receiving one next-visible
FIFO DLQ message. Receive uses a fresh attempt ID, zero wait and visibility
covering the bounded selection lifetime. Inspection changes that message's
visibility and receive count but never deletes, publishes, abandons or overwrites
delivery evidence. An empty queue returns no content. Failures expose fixed
safe messages and leave the message available for later visibility-timeout retry.

The response retains a valid command's exact raw idempotency key and notification
type, even when these contain private-bearing text. This approved exception is
limited to those two authenticated response fields. Other fields contain only
business/SQS/evidence timestamps, receive count, recipient digest, fixed parsing
or delivery-state classification, a statement that historical dependency errors
are unavailable, and a signed selection token. Do not expose recipient-address,
personalisation, template, rendered-content or Notify-response fields. Malformed
or unsupported commands expose no partially extracted command fields and no
usable selection. Logs retain the safe diagnostic-label rules above.

The shared-secret, versioned HMAC selection contains only FIFO receive-attempt
ID, opaque SQS message ID, HMAC queue binding, absolute UTC expiry and immutable
evidence digest. It contains neither raw command identity nor body/receipt
content, works across correctly configured hosts and is not persisted. Expiry
is anchored before receive, is strictly within AWS's five-minute attempt window,
and is not extended by a delayed response. Pass bounded cancellation to queue
and storage calls and reject late confirmations. Redrive uses this selection
contract; discard uses the same authenticated selection body.

`POST /admin/notification-commands/dlq/redrive` accepts the selection token in its
JSON body and requires the same Basic Admin policy. Invalid or expired tokens
return a fixed bad-request result before receiving. Replay uses the signed FIFO
receive-attempt ID, one message and zero wait. Missing, changed or malformed
selected commands return a fixed conflict without publishing or deleting. The
whole operation waits for migration readiness and shares one bounded dependency
deadline capped by the selection's remaining lifetime; late confirmations cannot
start the next effect.

Publish the exact source body and message attributes, including `Content-Encoding`,
with the canonical recipient lane. Use a versioned, domain-separated HMAC of
source DLQ URL and SQS message ID as transport deduplication identity. It remains
stable across hosts/retries and differs from the producer command-key identity,
so a consumed original copy's five-minute SQS deduplication memory cannot swallow
recovery. Keep the command idempotency key unchanged. Recovery joins current lane
order and does not rewrite delivery evidence or apply discard restrictions;
normal consumption still checks conflicts, active claims and terminal outcomes.

Only confirmed publication permits deletion of the replayed selected receipt.
Failed, timed-out or late publication leaves the source even if a destination
copy exists. Failed deletion retains publication; a repeat uses the same recovery
transport identity. Outside SQS's deduplication window, another copy remains
subject to durable command identity. Redrive responses and logs contain fixed
safe results, no raw command fields, receipt, selection token or dependency
exception text. No additional HTTP retries or Notify reconciliation are added.

`POST /admin/notification-commands/dlq/discard` applies the same authenticated,
bounded replay and command verification. Invalid, expired, changed or malformed
selections cannot create evidence or delete. Atomically record abandonment before
selected deletion. New records retain only the original eight minimal fields;
notification/recipient/immutable fields are keyed digests, notification type is
the configured safe diagnostic category or `other`, and timestamps/outcome record
abandonment. The immutable digest still covers the original type and delivery
identity. Never store raw key, recipient, personalisation or template in new
abandonment evidence.

A per-key unique index and Mongo server-time pipeline permit a new record or a
matching expired pending claim. Active claims, immutable conflicts, accepted or
suppressed records and unknown states return conflict without changing history
or deleting. An expired transition clears obsolete owner/lease fields and
preserves record identity; matching abandonment allows idempotent removal.
Claim recovery and acceptance cannot win the same atomic transition as
abandonment. Future consumer duplicates are acknowledged without Notify.

The entire discard operation shares its dependency timeout, capped by selection
expiry. Failed or late abandonment confirmation leaves the source even if the
write completed. Failed deletion retains durable abandonment, allowing a repeat
on another host. Abandonment prevents future attempts and does not establish
whether an earlier indeterminate Notify request succeeded. Responses/logs remain
fixed and safe; the raw inspection identity exception does not extend to discard
responses or persisted abandonment.

Floci lacks native receive-attempt replay. Local tests explicitly supply that
one API behavior with a test-only adapter while real FIFO queues verify
publication, deduplication, deletion and isolation, and real Mongo verifies
readiness and unchanged evidence. Native AWS replay remains a deployment
validation requirement.

Command queue/Mongo extended health runs when sending or administration is
enabled, with DLQ health added for administration. Notify health remains enabled
only for sending. `/health` and analytics remain independent of administration
readiness.

## Deployment ownership

- The analytics queue must be a separate SNS subscription from the producer
  queue, with its own redrive policy. Never reuse the producer queue.
- `AnalyticsEventConsumer__QueueUrl` and
  `AnalyticsEventConsumer__ProcessingEnabled` are deployment-owned settings.
- Command queue, MongoDB, cutover, digest-secret and `AWS_EMF_*` settings are also
  deployment-owned. Local Compose settings do not configure CDP environments.
  Set the actual collector endpoint explicitly for CDP/FluentBit; the pinned SDK's
  Fluent-host endpoint derivation is malformed.
- Administration enablement, DLQ URL, selection/timeout settings, Basic client
  credentials/scopes and operator routing are deployment-owned. Compose defaults
  do not configure CDP access.

## Behaviour verification

Unit tests cover parsing, validation, logging, deletion, and failure behaviour.
Compose-backed integration tests cover health and SNS-to-SQS-to-consumer wiring.
Test that stored command evidence and logs do not disclose protected identifiers,
addresses, templates, or personalisation. Analytics event and entity IDs are
logged as required by the analytics contract above.
