# Service behaviour

This document describes message contracts and processing requirements. Use
[CONTEXT.md](../CONTEXT.md) for terminology and the linked ADRs for decision
rationale. The command architecture is accepted; the cutover ADR is accepted. Current
implementation scope is described below.

## Current scope

The analytics consumer logs event and entity IDs and deletes successfully
processed messages. It does not deliver notifications or persist event data.
With the default null cutover, the command consumer durably suppresses every
valid command and deletes it without Notify. With a supplied boundary, it
records pre-cutover commands as `delivery-suppressed` and
sends at-or-after-cutover commands through GOV.UK Notify under a Mongo claim.
Notify acceptance must be recorded before SQS deletion. Matching accepted,
suppressed, or abandoned records suppress duplicates regardless of the current
cutover. Analytics does not yet create notification commands. Configured Basic or OAuth
administrators can inspect command-DLQ batches, redrive selected commands or discard one command-DLQ message. Redrive does not restore a command's
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
  A disabled analytics consumer logs once and awaits cancellation.
- Apply the logging restrictions in [coding standards](../CODING_STANDARDS.md).

## Notification commands and data protection

Producer wire fields, timestamp precision and exact FIFO lane calculation are
specified in the [producer contract](notification-command-producer-contract.md).

- Keep notification delivery separate from the analytics-event path. Analytics
  consumption does not deliver notifications, mutate business data, transform
  payloads, or persist event data.
- Validate commands before publishing or consuming them. Normalise a recipient
  only where the command contract requires it.
- Consume commands unconditionally after successful startup readiness. Null
  cutover permanently suppresses them; it does not pause consumption. Validate
  the configured FIFO queue, digest secrets, Notify credentials/API URL, complete
  processing budget and any supplied UTC cutover at
  startup. Invalid values and deployment placeholders prevent startup and queue
  effects. Digest creation independently rejects unconfigured secrets.
- Use the idempotency key as the FIFO message-deduplication ID and a
  non-reversible per-recipient digest as the FIFO message-group ID.
  Validate the key before publishing or consuming: it must contain 1–128
  characters from the ASCII letters, digits and punctuation allowed by
  [SQS SendMessage](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessage.html#API_SendMessage_RequestParameters).
  Reject invalid keys without changing them; never trim, truncate or replace
  the key to fit the queue constraints.
- Run versioned Mongo migrations under a renewable exclusive lease on every host. Critical migrations gate `/health`; migration 001 is
  critical because its full unique notification-key index enforces idempotency.
  Require the build to complete before recording history or readiness; catalog
  presence alone is insufficient. Reject incompatible existing definitions without
  dropping them. Matching completed hidden indexes satisfy the prerequisite.
  Confirm applied critical history and the latest critical schema once at
  startup. Both consumers await the first successful anonymous health response
  through a shared hosting boundary. Admin HTTP requests return 503 until the shared startup boundary is released.
  Stores and administration services have no completion dependency.
  This does not detect later manual schema changes. Completion by another host can satisfy this check, including
  after the local host exhausts its migration attempts. A lease-renewal failure
  cancels the engine; only after it stops can the host release and reacquire
  the lease. While critical readiness is incomplete, an attempt failure also
  relinquishes the lease after stopped work, followed by a five-second backoff.
  Once readiness is established, standard failures retry under the lease with
  the existing 30-second delay. Use a 30-second lease renewed every ten seconds.
  Attempts share one bounded host-wide budget across acquisitions and phases.
  Host shutdown cancels the migration engine but continues renewing its lease
  while renewal succeeds until execution stops.
  Bound acquisition and renewal confirmation by a deadline measured from the
  request start, reserving half a renewal interval before expiry. Cancel the
  engine independently of renewal I/O when confirmation misses that deadline;
  ignore late success and reject renewal of an expired lease. Await outstanding
  renewal work before local release or reacquisition. Cancellation-resistant
  engine operations can still outlive ownership loss; the lease provides no
  fencing after expiry.
  Bound each critical operation by 20 seconds and each standard operation by
  300 seconds. Preserve ordered prerequisites; a standard migration between
  two critical versions does not inherit the earlier critical deadline.
  Migration failures and
  prolonged readiness waits produce error logs. Unapplied critical migrations
  keep `/health` unhealthy and consumers paused.
- Use configured bounded diagnostic type labels in logs and command metrics;
  unconfigured notification types use `other`. Log fixed failure reasons and
  exception type names without exception objects, messages or response contents.
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
  Acceptance updates require the same owner and a pending outcome, even after
  lease expiry. Accepted evidence includes the opaque versioned HMAC Notify reference,
  template ID/version, Notify notification ID, correlation digests, and timestamps.
- Make one Notify request per claim with no HTTP retry or redirect. Normalize the
  recipient for the request. Give pooled connections a two-minute lifetime so
  the retained client refreshes DNS when opening replacement connections.
  Require `201 Created` and consistent minimal
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
  is 120 seconds and command leases are 90 seconds, leaving retry headroom; receive, claim, send, acceptance and deletion
  bounds are 30, 5, 60, 10 and 5 seconds, plus 10 seconds headroom. Measure elapsed
  time monotonically from receive/claim request starts and reject late confirmations
  before sending. Request visibility explicitly on receive without changing shared
  queue configuration. Pass cancellation through the entire Notify request and
  response buffering. A failed send retains its claim until expiry.
- A Notify timeout, lost response, crash, or failed acceptance write does not prove
  rejection. Queue retry after expiry may send a duplicate email. An already
  in-flight request can outlive ownership during a process stall; Mongo rejects
  acceptance from a replaced owner or terminal claim. A complete, valid Notify
  acceptance is persisted with a fresh bounded token even after send timeout,
  ownership-budget expiry or shutdown; it is never discarded just for lateness.
  No Notify-reference reconciliation is implemented. Queue
  deletion failure after durable acceptance retries as a terminal duplicate.
- Compare the immutable UTC business-action timestamp with the deployment-owned
  cutover value. Any supplied cutover and serialized action timestamps must
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

Failure logs retain a fixed reason category and the original exception type
name alongside permitted correlation. Categories distinguish invalid commands,
conflicts, active claims, Notify 4xx rejection, indeterminate sends, store/queue
errors, lost ownership, exhausted processing budgets and unexpected errors.
They contain no dependency exception text, inner exception or response body.
DLQ operators can correlate a selected message ID with these logs; historical
errors are not reconstructed or added to persisted delivery evidence.

Instruments follow Waste Obligations' DI-owned meter and singleton instrumentation
conventions. Shared names and tag keys use PascalCase; counters use CloudWatch
`COUNT` and claim/send durations use `MILLISECONDS`, matching email-send timing.
Command dimensions are the fixed Notifications `Service`, bounded
`NotificationType` and fixed `Outcome`. The process-wide exporter follows Waste
Obligations' static meter listener and EMF `MetricsLogger` mechanism, initialised
before the host starts. It observes the known command instruments by meter name.
One measurement emits one SDK document; logger disposal performs the flush.
The SDK owns its process-wide environment cache, platform metadata decoration,
agent transport and lifecycle.

Export defaults to enabled and requires a configured namespace, except that
`AWS_EMF_ENVIRONMENT=Local` allows a blank namespace and uses the Notifications
namespace. Disabled export does not resolve an SDK environment. Local development
and isolated tests disable it. SDK environment selection and routing read actual
process environment variables. No custom metadata transport, environment factory
or shutdown lifecycle is present. SDK internal diagnostics are disabled; export
failures expose a fixed message and exception type without private contents.
Metrics remain best effort. See the README for configuration and CDP routing.

`/health/all` includes a light read-only Notify check on every host, including
with null cutover. It makes one authenticated `GET /v2/templates?type=email`,
requires `200 OK`, and uses the pinned SDK template-list operation. The SDK
reads and deserializes template data; it is discarded without logging, persistence
or exposure in the health result. The existing ten-second health timeout cancels
the complete HTTP request and response buffering; a late response
after cancellation cannot report healthy. Results and logs expose only fixed
descriptions, without dependency exceptions or response data. `/health` stays independent
of extended dependency checks. This check does not validate a specific template
or confirm recipient delivery.

The command architecture is described in
[ADR 0001](adr/0001-notification-command-delivery-architecture.md), and the
cutover decision in [ADR 0002](adr/0002-email-delivery-cutover-boundary.md).

## Command-DLQ verification command

`POST /admin/notification-commands/dlq/verification-command` shares the Admin ACL and startup boundary. It accepts no payload, including framed/chunked bodies. Generate a fresh prefixed key with schema 1, action time 2000-01-01 UTC, type `admin-verification`, recipient `verification@example.invalid`, an all-zero template ID and empty personalisation. Validate and normalise the command before any effect. Confirm fresh permanent suppression before publishing one FIFO message to the configured DLQ, using the key for deduplication and the canonical recipient lane. One dependency deadline covers both effects; failures expose fixed 503 details. Rejected, cancelled or late suppression never publishes. Publication failure leaves suppression evidence intact. Return only the generated key and SQS message ID after timely confirmed publication.

Successful redrive is followed by ordinary consumer processing: matching terminal evidence suppresses Notify and the consumer deletes the source message without changing the record. This holds for null or earlier cutover values. Creation confirms queuing rather than completed processing. Inspection selects up to ten next-visible messages, so locate the generated identity in the returned batch before acting. Suppressed probes cannot be discarded; successful native discard remains separately unverified. Local replay uses the labelled controlled API adapter and does not prove native FIFO replay.

## Command-DLQ status

`GET /admin/notification-commands/dlq/status` uses the same Admin ACL and startup
boundary. It reads SQS attributes and returns approximate visible, in-flight,
delayed and total message counts, plus the latest AWS redrive task's status, start
time and approximate progress. It never receives messages or changes visibility.
Counts are eventually consistent; inspecting commands moves them into the in-flight
count until their visibility expires or they are removed. Missing or invalid
attributes and failed or late responses produce a fixed 503 result.

## Whole command-DLQ redrive

`POST /admin/notification-commands/dlq/redrive-all` uses the same Admin ACL and
startup boundary and accepts no body. Resolve both configured queues' ARNs and
start AWS `StartMessageMoveTask` with the DLQ as source and the command queue as
explicit destination. AWS chooses the transfer rate. A confirmed task returns
202 with its task handle and a Location pointing to the status endpoint; this
confirms task creation rather than completion or email delivery. AWS owns the task
and permits one active task per DLQ. No message content is read, transformed or
persisted by this endpoint, including malformed commands.

One dependency deadline covers ARN lookup and task creation. Failures and late
confirmations return a fixed 503; creation may still have succeeded, so check
status before retrying. AWS redrive operates on available messages. Let existing
inspection visibility periods expire before whole-queue recovery. Normal command
consumption still applies cutover, duplicate, conflict and claim rules; terminal
suppression or abandonment is not undone. Deployed IAM must permit redrive and task
listing as well as the source receive/delete and destination send operations.

## Command-DLQ inspection

Administration is always registered independently of sending.
`POST /admin/notification-commands/dlq/inspect` requires authenticated
Basic or Bearer credentials from the Waste Obligations `Acl.Clients` shape with
an ACL `admin` scope. Basic requires an ApiKey client and its secret; Bearer
requires exactly one `client_id` identifying an OAuth client. Unknown clients,
wrong client types, incorrect/malformed credentials, invalid lifetimes and
read/write-only clients cannot reach queue or record operations. Token-provided
scope or role claims cannot grant privileges; only configured ACL scopes apply. Validate
all configured ACL entries, distinct command/DLQ FIFO URLs and digest secrets at
startup. An empty ACL or one with no admin scope permits startup and denies all
administrator calls.
See [ADR 0004](adr/0004-command-dlq-inspection.md).

Bearer follows the explicitly approved Waste Obligations gateway trust contract:
the private CDP gateway validates Cognito signatures, issuer and audience. The
service parses tokens and validates lifetime with the framework's default
five-minute clock skew, but does not verify signature, issuer or audience.
Gateway authentication must protect every administrator route. Direct backend
callers can assert an ACL client identity; deployment-owned network controls
must establish that trust boundary. Private routing alone does not establish
gateway validation.
Another CDP service reaching the backend could forge an unexpired token for a
known OAuth administrator client ID and permanently abandon delivery. Client IDs
are not secrets. This consequence belongs to the explicitly approved gateway-only
contract and requires deployment-owned access controls for administrator access.

Every host consumes commands and starts the same Mongo migrations, including
with null cutover. Null permanently suppresses delivery; it does not pause consumption. Its HTTP boundary returns 503 until the first successful anonymous
`/health` response completes, before receiving up to ten next-visible
FIFO DLQ messages in one request. Receive uses a fresh attempt ID, zero wait and visibility
covering the bounded selection lifetime. Inspection changes those messages'
visibility and receive counts but never deletes, publishes, abandons or overwrites
delivery evidence. An empty queue returns no content. Failures expose fixed
safe messages and leave the message available for later visibility-timeout retry.

The response contains a `messages` array. Each valid entry exposes the original command's
schema version, idempotency key, notification type, action timestamp, normalised
recipient address, template ID and personalisation, plus SQS message ID, timestamps,
receive count, current delivery classification, recipient digest and selection token.
Command content is permitted only in authenticated inspection responses; it must
not appear in logs, metrics, tokens or Mongo evidence. Rendered emails and Notify
responses are not returned. Historical dependency errors are not persisted.
Malformed or unsupported commands expose only SQS metadata and a fixed
classification, with no partially extracted command fields or usable selection.

The shared-secret, versioned HMAC selection contains only FIFO receive-attempt
ID, opaque SQS message ID, HMAC queue binding, absolute UTC expiry, immutable
evidence digest and original receive visibility timeout. Batch format `v3` also
binds the original maximum receive size. Single-message format `v2` remains valid. Replay uses
the original size and timeout across hosts with different local settings. Old `v1`
selections require fresh inspection. Replay resets visibility and may leave the
message hidden after token expiry; it never extends authorization to act.
It contains neither raw command identity nor body/receipt
content, works across correctly configured hosts and is not persisted. Preserve the evidence secret across hosts and deployments while evidence, selections or replayable commands exist. It also derives Notify references and redrive deduplication IDs through distinct HMAC domains. Key rotation is unsupported; changing it can invalidate selections and duplicate detection. Expiry
is anchored before receive, is strictly within AWS's five-minute attempt window,
and is not extended by a delayed response. Pass bounded cancellation to queue
and storage calls and reject late confirmations. Redrive uses this selection
contract; discard uses the same authenticated selection body.

`POST /admin/notification-commands/dlq/redrive` accepts the selection token in its
JSON body and requires the same Basic/Bearer Admin policy. Invalid or expired tokens
return a fixed bad-request result before receiving. Replay uses the signed FIFO
receive-attempt ID, original maximum receive size and zero wait. Missing, changed or malformed
selected commands return a fixed conflict without publishing or deleting. The
whole operation shares one bounded dependency
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

Redrive also accepts `{ "selectionTokens": ["..."] }` for one to ten distinct
messages from the same inspection. Validate all tokens before queue effects and
replay the receive attempt once. Each selected command is independently verified,
published unchanged and deleted only after confirmed publication. Return a `messages`
array of SQS message IDs and fixed `redriven`, `unavailable` or `failed` outcomes.
A batch is not atomic; completed entries are retained when another entry fails.
Reinspect remaining messages after visibility expires rather than replaying a batch
whose messages have already been deleted. Mixed batches, duplicates, invalid or
expired tokens are rejected before receiving. The single-token request remains supported.

`POST /admin/notification-commands/dlq/discard` applies the same authenticated,
bounded replay and command verification. Invalid, expired, changed or malformed
selections cannot create evidence or delete. Atomically record abandonment before
selected deletion. New records retain only the original eight minimal fields;
notification/recipient/immutable fields are keyed digests, notification type is
the exact command type consistent with accepted/suppressed records, and timestamps/outcome record
abandonment. The immutable digest still covers the original type and delivery
identity. Never store raw key, recipient, personalisation or template in new
abandonment evidence. The notification type can contain private-bearing text;
the safe diagnostic projection still applies to logs and metrics.
Existing abandoned records retain their stored label without backfill or history
rewrite; their original type may no longer be recoverable.

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
responses. Only notification type is stored raw, matching other delivery outcomes.

Floci lacks native receive-attempt replay. Local tests explicitly supply that
one API behavior with a test-only adapter while real FIFO queues verify
publication, deduplication, deletion and isolation, and real Mongo verifies
readiness and unchanged evidence. Native AWS replay remains a deployment
validation requirement.

Command queue/DLQ/Mongo and Notify extended health run on every host, including
with null cutover. Critical migrations gate `/health`; both consumers and
administration start after its first successful anonymous response completes.

## Deployment ownership

- The analytics queue must be a separate SNS subscription from the producer
  queue, with its own redrive policy. Never reuse the producer queue.
- `AnalyticsEventConsumer__QueueUrl` and
  `AnalyticsEventConsumer__ProcessingEnabled` are deployment-owned settings.
- Command queue, MongoDB, cutover, digest-secret and `AWS_EMF_*` settings are also
  deployment-owned. Local Compose settings do not configure CDP environments.
  Set the actual collector endpoint explicitly for CDP/FluentBit; the pinned SDK's
  Fluent-host endpoint derivation is malformed.
- Administration DLQ URL, selection/timeout settings, Basic/OAuth client
  credentials/scopes and operator routing are deployment-owned. Compose defaults
  do not configure CDP access.

## Behaviour verification

Unit tests cover parsing, validation, logging, deletion, and failure behaviour.
Compose-backed integration tests cover health and SNS-to-SQS-to-consumer wiring.
Test that stored command evidence and logs do not disclose protected identifiers,
addresses, templates, or personalisation. Analytics event and entity IDs are
logged as required by the analytics contract above.

## Initial deployment and cutover

The default cutover is null. Consumption suppresses all valid commands
without a Notify send while Waste Obligations retains direct delivery. Suppression
must be durable before deletion; malformed commands, conflicts and persistence
failures retain the message. Administration cannot clear malformed commands or
immutable conflicts; configured queue retention or deployment-owned controlled
removal is required. This service supplies no verified removal tool for those
messages. Existing suppressed evidence is terminal when a
future cutover is configured. A non-null cutover requires explicit UTC and the
same millisecond precision as command identity. Other configuration requirements
remain in force.

After the producer dry run, configure Notifications with a future X first.
Verify every active Notifications host has X and the post-cutover delivery
implementation, and stop every old null-cutover consumer. Then configure Waste
Obligations with the identical X and verify its rollout completes before X.
After X, use Notifications recovery and do not clear or change the cutover.
ADR0002 records this accepted, forward-only handover. Local examples do not
configure deployed values.

Critical migration completion is reported by the `MongoMigrationCompletion`
entry in `/health/all` and gates `/health` for every host.
Migration 001 is flagged `Critical = true`; migrations default to non-critical.
An absent or invalid critical prerequisite returns 503, including when another
host owns the lease or every host exhausts its attempts. Already-applied critical
migrations satisfy readiness without being rerun. Non-critical failures do not
block readiness. The latest critical migration owns the current schema validation;
older critical history remains required without retaining obsolete schema checks.

HTTP and migration work start first. Both consumers wait through a shared hosting
boundary until this host completes its first successful anonymous `/health`
response. `/health/all` and `/health/authorized` do not release that boundary.
Stores and business operations have no migration checks. Health reads a latched
startup result and performs no Mongo queries; later dependency outages are
reported by extended health. CDP controls replacement of hosts that remain
unhealthy. Long critical migrations must fit the actual platform startup budget
or be applied before rollout; the documented 95 seconds is not an overall
deployment deadline. Metrics and startup logging remain available for diagnosis.

Suppression recorded with an unset cutover remains terminal after configuration,
including an action at or after the new boundary. Delete matching replays without
changing the record. Fresh post-cutover commands follow the claimed Notify delivery path.
