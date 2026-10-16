# Waste Obligations Notifications

A CDP service that consumes Waste Obligations analytics events and delivers email notification commands.

## Scope

The service receives every message from its service-owned SQS subscription to the
`waste_obligations_analytics_events` SNS topic. It enriches compliance-declaration
submission events through the Waste Obligations read API and publishes submitted-email
commands before acknowledging them. Other valid analytics messages retain their
log-and-delete behaviour. Analytics does not send email directly or persist events.

With the default null cutover, the command consumer durably suppresses every
valid command and deletes it without Notify. With a supplied boundary, it
records pre-cutover commands as `delivery-suppressed` and
sends at-or-after-cutover commands through GOV.UK Notify. It acquires a Mongo
claim, makes one send request, records acceptance, and then deletes the command.
Matching accepted, suppressed, or abandoned commands are deleted without another
send. Conflicts, active claims, failed sends and incomplete persistence remain on
SQS for visibility-timeout retry and queue redrive. A redriven command does not
regain its original recipient-lane position.

`GET /admin/notification-commands/dlq/status` returns approximate visible, in-flight,
delayed and total DLQ counts and the latest AWS redrive task's progress without
receiving messages. `POST /admin/notification-commands/dlq/redrive-all` accepts no
body and starts native AWS redrive to the configured command queue, returning 202.
Let inspection visibility expire before whole-queue recovery.

Configured Basic or OAuth administrators can inspect up to ten next-visible command-DLQ messages
through `POST /admin/notification-commands/dlq/inspect`. Administration is always
registered; an empty ACL denies access. Inspection returns email inputs and a signed expiring selection for each command;
`POST /admin/notification-commands/dlq/redrive` accepts one selection or a batch of selections in a JSON
body, republishes the unchanged command, then removes only the selected source
after publication is confirmed. Neither operation changes delivery evidence.
Inspection temporarily changes visibility and receive count.
`POST /admin/notification-commands/dlq/discard` records eligible abandonment before
removing the selected source, so future duplicates remain suppressed.

Notify acceptance means Notify accepted the email request; it does not prove
recipient delivery. A timeout, lost response, crash, or persistence failure can
leave an indeterminate send. After claim expiry, a queue retry may send that email
again. There is no HTTP retry or Notify-reference reconciliation in this service.

Email sends use the pinned `GovukNotify` 8.1.0 client, following Waste Obligations'
injectable client factory. The SDK owns authentication, request serialization
and response models. A transport adapter keeps the configured API routing,
cancels the complete HTTP request and response buffering, requires `201 Created`,
and disposes each operation's request and response. Personalisation retains its
JSON values through SDK serialization; only minimal acceptance evidence leaves
the client boundary. The SDK's synchronous wait runs off the consumer caller,
and dependency exceptions are replaced with fixed safe failures.
Notify connections have a two-minute pooled lifetime, so the retained client
resolves DNS again when opening replacement connections.

New suppression records retain their original eight-field shape, omitting absent
lease and Notify fields. Claims and accepted records retain their additional
evidence; this change does not rewrite existing documents.

Normal publication uses the idempotency key unchanged as the FIFO deduplication ID. Publishing
and consumption reject keys longer than 128 characters or containing whitespace,
control characters, or characters outside the ASCII letters, digits and punctuation
allowed by [SQS SendMessage](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessage.html#API_SendMessage_RequestParameters).

Malformed messages, messages without `eventId` or `entityId`, and unsupported
content encodings are not deleted. The service-owned SQS queue's CDP redrive
configuration routes them to its convention-led dead-letter queue after the
configured receive attempts are exhausted.

## HTTP requests

Run [health and command-DLQ requests](runbooks/http/README.md) in Rider or VS Code
with private settings per deployed environment. Select `docker` to use the local
Compose service and its development Basic-auth client.

## Prerequisites

- .NET 10 SDK
- Docker or a compatible container runtime

## Run locally

Start the Consumer and local Floci SNS/SQS resources:

```bash
docker compose up --build
```

The local bootstrap creates `waste_obligations_analytics_events` and subscribes
`waste_obligations_notifications_analytics_events_queue` with raw message delivery.
It also creates isolated FIFO command and command dead-letter queues, MongoDB,
and a controlled Notify HTTP fixture. The fixture uses a dummy API key, verifies
the health request's JWT, and records send requests only in memory for local
integration tests; it does not contact GOV.UK Notify. Its port is `8086`.

The Consumer health endpoint is available at `http://localhost:8085/health`.

## Documentation

- [Coding standards](CODING_STANDARDS.md): code structure, style, security, and test conventions.
- [Contributing](CONTRIBUTING.md): formatting, required checks, and change workflow.
- [Service behaviour](docs/service-behaviour.md): message contracts, processing rules, and deployment ownership.
- [Context](CONTEXT.md): notification-delivery terminology.
- ADRs: accepted [command architecture](docs/adr/0001-notification-command-delivery-architecture.md) and accepted [cutover boundary](docs/adr/0002-email-delivery-cutover-boundary.md).
- Accepted [administrator inspection](docs/adr/0004-command-dlq-inspection.md): Basic access and content-free selection.
- [Agent guidelines](AGENTS.md): entry points and sandbox build guidance for coding agents.

## Test

See [Contributing](CONTRIBUTING.md#required-checks) for the required local build,
unit tests, integration tests, and Compose teardown.

## Configuration

`AnalyticsEventConsumer` is disabled by default. CDP deployment configuration must
set `AnalyticsEventConsumer__ProcessingEnabled` to `true` and provide the
service-owned `AnalyticsEventConsumer__QueueUrl`. The deployed queue must be a
separate subscription from the producer queue and must have the CDP dead-letter
queue convention configured. Each host requests one message per receive;
normal consumers have no batch-size setting. A larger receive requires a reviewed
processing/visibility budget. Analytics immediately receives again after success
or an empty response. `AnalyticsEventConsumer__PollIntervalSeconds` controls
cancellation-aware error backoff only; SQS long polling supplies idle waiting.

Submitted compliance-declaration events read the persisted declaration and publish
commands using the first `Submitted` audit and stored organisation fields. Latest
Accepted/Cancelled status does not replace the original submission. Outside-scope
events retain their existing acknowledgement behaviour. Read/validation/publication
failures are retried; an acknowledgement failure after confirmed publication can
cause a matching replay. Publication and Notify acceptance do not prove delivery.

Analytics has a separate processing budget. `AnalyticsEventConsumer__ReceiveTimeoutSeconds`
(default 30) must exceed its long poll. `WaitTimeSeconds` must be 1–20 seconds;
zero is rejected to prevent continuous empty receives. `ReadTimeoutSeconds` (20),
`PublishTimeoutSeconds` (10), `DeleteTimeoutSeconds` (5) and `SafetyHeadroomSeconds`
(10), plus receive time, must fit `VisibilityTimeoutSeconds` (90). These settings
share the `AnalyticsEventConsumer__` environment prefix. Each receive requests
explicit visibility; increasing batching requires a reviewed budget change.

Before this image reaches an environment with analytics processing already enabled,
provision the Waste Obligations read address, credentials and Read ACL. Alternatively,
confirm `AnalyticsEventConsumer__ProcessingEnabled=false` before deployment.
Missing read configuration with processing enabled rejects startup for the whole
host, including command delivery and DLQ administration.

Configure `WasteObligations__BaseAddress`, `WasteObligations__Username` and
`WasteObligations__Password` for Waste Obligations Basic Read access. Disabled analytics
needs no read credentials. Local development values are synthetic examples, not a
running declaration API. HTTPS requests prefer HTTP/2 with HTTP/1.1 fallback.
The service client retries transient GET failures up to twice with exponential
jitter, within a ten-second total and three-second attempt timeout; the analytics
read budget and cancellation still bound the complete operation. `DeclarationRead` extended health calls `health/authorized` with Basic credentials;
successful authentication does not prove the deployed Read ACL or MO-562
version/identity response; validate both before activation.
Compose disables analytics processing because it has no declaration API. The
submitted-email integration scenarios enable their own hosts with a controlled
declaration endpoint. To process submissions locally, configure a reachable Read
service and explicitly enable analytics processing.

The four overridable UUID mappings are:

| Environment setting | Default template |
| --- | --- |
| `Submission__DirectEnglishTemplateId` | `5f64e3bd-d454-4a45-a9c6-9409bf940d7a` |
| `Submission__DirectWelshTemplateId` | `b3223b0b-a467-40c1-9150-f78b76d11fd8` |
| `Submission__SchemeEnglishTemplateId` | `b103685d-de8a-4ea9-abc8-d244ca26841b` |
| `Submission__SchemeWelshTemplateId` | `95d8c2aa-a229-4ecb-9774-78ec06d0cbac` |

Only persisted `GB-WLS` selects Welsh, and `ComplianceScheme` selects scheme.
The five fields are integer `obligationYear` and strings `regulatorLeading`,
`regulatorInline`, `regulatorEmail`, `user`. English regulator articles are `The `
and `the `; Welsh preserves the stored regulator without articles. Original audit
recipient/name/time take precedence over current actor, locale or submitter name.

Publication logs connect safe analytics IDs to the opaque Notify reference, template,
variant and original action time. Delivery logs distinguish null/before-cutover
suppression, recorded Notify acceptance and terminal duplicate without claiming a
new send for a duplicate. Retry warnings name a fixed stage/reason and acknowledge
uncertain deletion accurately, without dependency text or PII. Existing metrics allow
four bounded categories: `declaration-submitted-direct-en`,
`declaration-submitted-direct-cy`, `declaration-submitted-scheme-en`,
`declaration-submitted-scheme-cy`. Keep these in the deployed
`NotificationCommandDelivery__DiagnosticNotificationTypes__0` (and subsequent indices)
allowlist to distinguish variants.

Finalize all template overrides before the producer dry run begins. Changing a
template ID for an existing submission creates an immutable-command conflict on
replay, which follows the command retry/DLQ policy.

Use null cutover for the producer dry run: commands are published and terminally
suppressed. Read ACL/deployed DTO, effective template overrides/account access and
queue redrive policies remain activation prerequisites. Shared live handover remains
gated on both submitted and cancellation producers.

`NotificationCommandDelivery` is deployment-owned. Every host consumes commands
after successful startup readiness; there is no command-processing enablement
flag. Before deployment, provide the FIFO queue URL, optional cutover, distinct
evidence-digest and recipient-lane secrets, Mongo connectivity/permissions and Notify credentials.
Set `Notify__ApiKey` to the service's Notify API key; `Notify__BaseAddress` defaults
to the GOV.UK Notify API. Every host validates the SDK key shape, API URL and
complete send budget, including with null cutover. Queue and secret placeholders
prevent startup. Null cutover permanently
suppresses commands; it does not pause consumption. Keep secrets outside source
control and logs. Digest creation also rejects unconfigured secrets when the
publisher is used independently.
Verify the target environment's effective configuration and access before merging
when [CDP auto-deploy](https://github.com/DEFRA/cdp-documentation/blob/main/how-to/auto-deployment.md) is enabled.
Cutover configuration and serialized `actionOccurredAtUtc` values must include
`Z` or a numeric zero offset (`+00:00` or `-00:00`). Offset-free timestamps are
rejected regardless of host timezone; nonzero offsets are rejected without
conversion. Both timestamps are truncated to whole milliseconds before comparison,
matching MongoDB storage precision. Command serialisation and immutable evidence
use the same precision so a Mongo roundtrip does not change command identity.

Logs and command metric tags use `NotificationCommandDelivery__DiagnosticNotificationTypes`
(an optional list of at most 32 lowercase ASCII category labels, each at most 64
characters). Unconfigured types use `other`. Failure logs contain a fixed cause
and exception type, without retaining dependency exception text or payloads.
Delivery evidence retains the command type and identity digests, outside diagnostic output.

Command consumption starts the next receive immediately after success or an
empty response. `NotificationCommandDelivery__PollIntervalSeconds` is the
error backoff only (15 seconds initially). Each host processes one command
at a time. `WaitTimeSeconds` defaults to a 20-second long poll;
`ReceiveTimeoutSeconds` bounds the entire receive request, including SDK
retries, and defaults to 30 seconds. It must exceed `WaitTimeSeconds`. These
command settings are separate from analytics settings and the shared SQS client.
CDP can override them through the `NotificationCommandDelivery` section;
local Compose values do not configure deployed environments.

Each receive requests `VisibilityTimeoutSeconds` explicitly (120 seconds initially).
The delivery claim is separate from migration leases and defaults to 90 seconds, leaving
30 seconds before visibility expiry so prompt redelivery can acquire a new claim.
`ClaimTimeoutSeconds`, `NotifyTimeoutSeconds`, `AcceptanceTimeoutSeconds`, and
`DeleteTimeoutSeconds` initially bound operations to 5, 60, 10, and 5 seconds.
`SafetyHeadroomSeconds` adds 10 seconds. Startup requires the command lease to
cover claim plus send, persistence, deletion and headroom (90 seconds), and visibility to cover
that budget plus the conservative 30-second receive bound: 120 seconds in total.
The receive and claim clocks start before their dependency requests; late
confirmations cannot start a send. Mongo uses its own clock for claim expiry and
owner and pending-outcome checks when acceptance is recorded. Confirmed acceptance
is always persisted with its own bounded token, even if the send timeout, lease
or shutdown cancellation has elapsed; a replaced owner cannot overwrite evidence.
Failed or indeterminate sends retain
the claim until expiry; the service does not release it early.

These values are initial estimates. Measure dependency latency and validate the
entire budget before deployment. Configure the deployed FIFO queue visibility and
DLQ redrive policy separately; local Compose sets visibility to 120 seconds and
three receives. Delivery does not mutate deployed queue settings. Dependency
cancellation bounds ordinary request work, but a process stall can let an already
in-flight request outlive ownership; acceptance remains fenced by Mongo and the
indeterminate-send limitation above still applies.

Delivery diagnostics use `NotificationType` labels from
`NotificationCommandDelivery__DiagnosticNotificationTypes` (array entries use
`__0`, `__1`, and so on). Configure trusted, non-PII category names: at most 32
labels, each 1–64 lowercase ASCII letters, digits or hyphens. The default list is
empty. Unknown values use `other` in metrics and operational logs; this changes
no command data, validation or immutable identity. Local settings show the two
declaration categories as diagnostic examples.

Failures include fixed `FailureReason` labels and the original `ExceptionType`
name: `invalid-command`, `conflict`, `active-claim`, `notify-rejected-4xx`,
`notify-indeterminate`, `store-error`, `queue-error`, `ownership-lost`,
`processing-timeout` or `unexpected-error`. No exception text, inner exception
or response content is logged. Operators can search these logs using the DLQ
message ID; historical failure details are not stored or reconstructed.

The `Defra.WasteObligationsNotifications` meter follows Waste Obligations'
DI-managed `IMeterFactory` convention. Singleton command instrumentation uses
shared PascalCase instrument and tag names, `COUNT` counters and `MILLISECONDS`
claim/send-duration histograms. Milliseconds match Waste Obligations' email-send
timing and also measure the short Mongo claim operation. Instruments cover
received commands, terminal outcomes, lease-claim outcomes and duration, Notify
accepted and failed sends, send duration, and terminal-duplicate suppression.
Tags contain only the fixed `Service=waste-obligations-notifications` value,
bounded `NotificationType` category and fixed `Outcome` values. A send acceptance
metric means Notify returned valid acceptance evidence, even if recording it
subsequently fails. A send failure metric means the attempt did not confirm acceptance, including timeout or shutdown
cancellation; it does not prove Notify rejected the email. Persistence failures
remain errors in operational logs, and failed claims receive a fixed failure
outcome.

The process-wide exporter follows Waste Obligations' static meter listener and
CloudWatch EMF 2.2.0 `MetricsLogger`. It is initialised before the host starts and
observes the command instruments by meter name. Each measurement uses the SDK's
normal environment provider and emits one document. The pinned SDK flushes on
logger disposal, so the exporter does not explicitly flush a second time.
Command dimensions stay bounded; the SDK owns platform metadata decoration,
environment caching and agent transport. SDK internal diagnostic logging is
disabled; exporter failures log a fixed message and exception type only.

EMF configuration uses the same root keys as Waste Obligations:

| Setting | Default and behaviour |
| --- | --- |
| `AWS_EMF_ENABLED` | `true`; Development, Compose and isolated tests disable export. |
| `AWS_EMF_NAMESPACE` | Required when enabled; the deployment placeholder is rejected. `Local` permits a blank value and uses `Defra.WasteObligationsNotifications`. |
| `AWS_EMF_ENVIRONMENT` | SDK process environment: `Local`, `Lambda`, `Agent`, `ECS` or `EC2`; unset/unknown uses SDK discovery. |
| `AWS_EMF_AGENT_ENDPOINT` | SDK process environment; configure the actual CDP collector endpoint. |
| `AWS_EMF_AGENT_BUFFER_SIZE` | SDK process environment; defaults to `100` documents. |
| `AWS_EMF_SERVICE_NAME`, `AWS_EMF_SERVICE_TYPE` | SDK process environment for platform metadata; the command `Service` dimension remains fixed. |
| `AWS_EMF_LOG_GROUP_NAME`, `AWS_EMF_LOG_STREAM_NAME` | SDK process environment for agent routing. |

The application validates enablement and namespace. Environment discovery and
routing use the SDK's actual process environment, rather than projecting .NET
configuration into a custom environment factory. No custom metadata client,
startup discovery deadline or sink shutdown lifecycle remains. As in Waste
Obligations, the SDK owns those behaviours and its process-wide cache. Metrics
are best effort; pending agent metrics may be lost when the process exits.

Set the namespace, environment and collector routing in CDP separately; local
settings do not configure deployments. For CDP/FluentBit, specify
`AWS_EMF_AGENT_ENDPOINT` rather than relying on `FLUENT_HOST` endpoint derivation:
the pinned SDK's [ECS implementation](https://github.com/awslabs/aws-embedded-metrics-dotnet/blob/v2.2.0/src/Amazon.CloudWatch.EMF/Environment/ECSEnvironment.cs)
builds an invalid derived endpoint. `AWS_EMF_ENVIRONMENT=Agent` also avoids metadata
discovery when the collector route is already known.

Each health request retains one Information `Request finished` event; other routine
Information events for health requests are filtered. Any CDP override of
`Microsoft.AspNetCore.Hosting.Diagnostics` must allow Information level.

Mongo migrations use the same versioned engine and renewable exclusive lease as
Waste Obligations. Migration 001 creates the unique `notificationKey_unique`
index on `NotificationDeliveryRecord`, preserving an existing matching index.
A critical index must have completed building, not merely appear in the catalog.
Incompatible existing definitions are rejected without dropping them. A retry
confirms an unfinished matching build before saving migration history; completed
hidden indexes remain valid.
Each host confirms critical history and the latest critical migration's schema
before `/health` can succeed. Both consumers start only after the first successful
health response. Record stores do not wait on migrations. A peer can establish
the prerequisite; non-critical failures do not block deployment readiness.
Failures are retried up to `MongoMigrations__MaximumAttempts` across all lease
acquisitions and phase changes on the host. While critical prerequisites are
incomplete, a failed attempt relinquishes the lease after engine and renewal
work stop, then waits five seconds before reacquiring. Once critical readiness
is established, standard failures retain the lease and the 30-second retry delay. A renewal error cancels the attempt; once the engine
stops, the host releases the lease and can reacquire it using the remaining
attempt budget. After exhaustion,
the host releases the lease and continues checking for completion by another host.
It needs a restart to make further migration attempts itself. Failed attempts,
exhaustion and prolonged readiness waits produce error logs for support alerts.
Each critical migration operation has a 20-second cooperative deadline; standard
operations retain a 300-second deadline. Each ordered operation receives its own
budget, including standard work between two critical migrations. A deadline or
host shutdown requests cancellation and continues renewing
the lease while renewal succeeds until execution stops; a migration that does not
stop needs support intervention.

Lease confirmation deadlines are measured from the start of acquisition and
renewal requests, reserving half a renewal interval for cancellation before
expiry. An independent deadline cancels the engine even when renewal I/O stalls;
late confirmations cannot restart that attempt. Renewal also rejects an expired
lease using MongoDB's current time. Local release and reacquisition wait for both
the engine and outstanding renewal work to stop. This requests cancellation before
lease expiry; the migration engine has synchronous operations that can outlive
cancellation, and the lease does not fence those operations after ownership is lost.

`MongoMigrations` configures lease duration, renewal interval, standard operation timeout,
retry delay and readiness-wait alert threshold in seconds (the latter uses
`LeaseAcquisitionAlertThresholdSeconds`). The defaults are
30, 10, 300, 30 and 300 respectively, with three attempts.
`CriticalOperationTimeoutSeconds` defaults to 20; `AttemptTimeoutSeconds` is the
per-operation standard budget, rather than a deadline for the whole migration chain. Renewal must be no
more than half the lease duration. CDP can override these defaults separately
from local Compose configuration.

Mongo connection settings use `Mongo__DatabaseUri` and `Mongo__DatabaseName`,
matching Waste Obligations. The default database is
`waste-obligations-notifications`, including in local Compose.
These replace
`NotificationCommandDelivery__MongoConnectionString` and
`NotificationCommandDelivery__MongoDatabaseName`; update CDP configuration before
rolling out this change. Keep the database separate from the Waste Obligations
API database so migration histories and leases remain service-owned.

For CDP, supply the complete Mongo URI with `authSource=$external` and
`authMechanism=MONGODB-AWS`, plus the platform-required TLS and topology options.
The AWS authentication provider obtains credentials from the task's credential
chain; do not put credentials in the URI. The task role must be authorised for
this service's database, including migration metadata, leases and index creation.
`TRUSTSTORE_` certificate variables are loaded before Mongo clients are created.
The client identifies itself as `waste-obligiations-notifications-consumer` and uses primary
reads for delivery evidence, even if the URI specifies another read preference.

Local Compose uses unauthenticated standalone Mongo and cannot verify CDP IAM or
TLS. Before deploying to CDP, verify authentication, certificate
loading, database permissions, migration completion and `/health/all`. Mongo
migrations and critical health checks run for every host.

`/health/all` checks GOV.UK Notify on every host, including with null cutover, with
one authenticated `GET /v2/templates?type=email`, bounded by the existing
ten-second health timeout. It uses the SDK template-list operation to check
connectivity and credentials. The SDK reads and deserializes the response within
the same cancellation bound; template content is discarded and never exposed or
logged. This does not validate a command's template or confirm email delivery.
Failures expose a fixed description without dependency error details.
`/health`
remains independent of Notify and the other extended dependency checks.

Whole-queue redrive requires `sqs:StartMessageMoveTask`, `sqs:ReceiveMessage`,
`sqs:DeleteMessage` and `sqs:GetQueueAttributes` on the DLQ, plus `sqs:SendMessage`
and `sqs:GetQueueAttributes` on the command queue. Status also requires
`sqs:ListMessageMoveTasks` on the DLQ. SSE-KMS queues additionally require the
applicable `kms:Decrypt` and `kms:GenerateDataKey` permissions. These are deployment
permissions; the endpoints introduce no new application settings.

Inspection, redrive and discard are always registered. Every host requires the administration FIFO `QueueUrl`, a distinct command FIFO URL, both delivery digest secrets, Mongo connectivity and valid Notify credentials/API URL and sending budgets, including with null cutover. `SelectionLifetimeSeconds` defaults to 120 and must be strictly below AWS's 300-second receive-attempt window; `DependencyTimeoutSeconds` defaults to 10 and must be positive and shorter than the selection lifetime. Administrator requests return 503 until critical migrations complete and the first successful anonymous `/health` response finishes. Redrive and discard share one dependency deadline capped by the signed selection's remaining lifetime. Null cutover permanently suppresses commands while consumption continues. There is no administration enablement switch.

The ACL follows Waste Obligations: `Acl__Clients__<clientId>__Type=ApiKey`,
`Acl__Clients__<clientId>__Secret`, and `Acl__Clients__<clientId>__Scopes__0=admin`.
Every configured client entry is validated. An empty ACL or one without an admin
allows startup but denies administrator access. OAuth follows Waste Obligations: set
`Acl__Clients__<clientId>__Type=OAuth` and
`Acl__Clients__<clientId>__Scopes__0=admin`; an OAuth entry needs no Basic secret.
Bearer tokens must identify exactly one configured OAuth client through
`client_id`. Only ACL scopes grant admin access; incoming token scope/role claims
cannot elevate a caller. ApiKey clients cannot use Bearer and OAuth clients
cannot use Basic. Basic credentials use UTF-8 and split at
the first colon, permitting colons in a secret. Supply secrets through CDP,
never source control. CDP operator routing and credentials must be configured
separately; local Compose uses an empty ACL to deny administrator access.

As explicitly approved, Bearer validation matches Waste Obligations: the private
CDP gateway validates Cognito signatures, issuer and audience; this service
parses the forwarded JWT and applies framework lifetime checks with the default
five-minute clock skew. It does not verify signatures, issuer or audience.
Gateway Cognito authentication must cover every administrator route. Direct
backend callers can assert an ACL client identity, so backend network access is
a deployment-owned trust boundary. This includes another CDP service forging
an unexpired token for a known OAuth admin client ID: client IDs are not secrets,
and that caller could permanently abandon delivery. The user explicitly chose
this service's gateway-only validation contract; enabling administration requires
deployment owners to accept and control this direct-access risk. Private DNS alone does not prove gateway
traversal. Configure gateway authentication, OAuth client IDs/ACL scopes and
network restrictions separately; this PR does not provision CDP resources.

Authenticated inspection returns a `messages` array with each valid command's email
inputs, SQS metadata, current delivery classification and signed selection token.
Recipient addresses, template IDs and personalisation are visible to the operator;
they remain excluded from logs, tokens and Mongo evidence.
Inspection adds current-state `operatorGuidance`, implemented `availableActions`
(method/endpoint/description), matching `deliveryRecord` summaries and available
Notify correlation. Encoded delivery-record links support retained lookup.
Pending/unrecorded items provide the
original Notify reference for investigation, without asserting acceptance.
Historical dependency errors are not persisted; inspection returns its current
classification without a placeholder failure-details field. Malformed or unsupported
commands disclose no partial command identity, summary, correlation or navigation,
and receive no usable selection or action. Conflicts do not expose mismatched
records as matching evidence. Guidance separates recorded acceptance from
recipient delivery and conditional investigation from historical failure causes.
Accepted redrive cleans up terminal queue copies. Explicit resend requests one
stable replacement after the operator checks Notify and any existing replacement.
Deploy compatible command consumers on every active host before using resend.
See the [operator matrix](docs/service-behaviour.md#command-dlq-inspection)
for guarded recovery choices and uncertain-Notify duplicate risk.
Logs use the diagnostic category allowlist and never expose raw identities.

Selections contain only receive-attempt ID, SQS message ID, an HMAC queue binding,
absolute expiry, immutable-field digest, opaque command/parent keys and the original receive visibility timeout. Hosts sharing the evidence secret
and DLQ configuration can validate them; no message body or receipt handle is
included in the selection or stored. Expiry starts before the receive request, and late dependency
confirmations fail safely. `/health/all` always checks the DLQ and Notify, including with null cutover. `/health` gates critical migrations; the remaining dependency checks are extended health.

Inspection issues format `v4`, binding the opaque source key and optional parent
as well as receive size and visibility timeout. `v2`/`v3` selections remain valid
for redrive/discard but require fresh inspection for resend. Another host
replays the same receive parameters even with different local selection settings.
Old `v1` tokens require fresh inspection. Replay resets SQS visibility to the
original timeout; this can keep the message hidden after the selection expires.
The signed expiry still limits every operation and is never extended.

Redrive accepts `{ "selectionToken": "..." }` or `{ "selectionTokens": ["..."] }`
in the authenticated POST body. Batch redrive returns per-message outcomes; reinspect
remaining messages after partial failure and visibility expiry.
It replays the selected FIFO receive attempt and verifies message identity and
immutable evidence before publishing the exact body and content encoding to the
canonical recipient lane. Its transport deduplication ID is a domain-separated
HMAC of source DLQ and SQS message ID, stable across retries and different from
normal command-key publication. This permits recovery inside the original FIFO
deduplication window after the original copy has been consumed. Recovery joins
the current lane order. Normal consumption still enforces immutable conflicts,
active claims and accepted/suppressed/abandoned outcomes.

A failed, timed-out or late publication confirmation leaves the source; a
destination copy may already exist. Failed deletion retains confirmed publication,
and a retry uses the same recovery deduplication ID. There is no additional
request retry. A later retry outside SQS's deduplication window may create another
copy, protected by normal durable command identity. Responses and logs use fixed
safe outcomes and expose no command content, receipt or selection token.

Discard accepts the same selection body and revalidates the selected command.
It atomically creates minimal `delivery-abandoned` evidence or transitions a
matching expired pending claim using Mongo server time. Active claims, immutable
conflicts, accepted/suppressed records and unknown states return a fixed conflict
without changing history or deleting the message. Matching abandonment permits
idempotent removal. New abandonment evidence stores digests, action/outcome
timestamps and the exact notification type, consistent with accepted/suppressed
records. The type may contain private-bearing text; logs and metrics still use
only the configured safe diagnostic category. Its immutable digest retains the
original command identity. It contains no raw key, recipient, template or body.
Existing abandoned records retain their earlier diagnostic label; no backfill
or terminal-history rewrite is performed, and the original type may be unavailable.

Only confirmed abandonment permits selected deletion. A failed or late write
leaves the source even when the write took effect; inspection/retry can observe
that evidence. Failed deletion retains abandonment and permits completion on
another correctly configured host. Abandonment suppresses future attempts; it
does not prove an earlier indeterminate Notify request failed.

Local administrator tests label their controlled `ReceiveMessage` replay adapter because
Floci does not implement native receive-attempt replay. FIFO publication,
deduplication, deletion, selected-message isolation and Mongo readiness/evidence
remain real. Native AWS replay must be checked during deployment validation;
local tests do not modify shared resources.

Native replay remains unverified. Follow the
[native SQS verification runbook](docs/command-dlq-native-sqs-verification.md)
to record deployment evidence; local checks cannot supply native proof.
The user approved always-registered authenticated administration despite this evidence gap.

Authenticated `POST /admin/notification-commands/dlq/verification-command` accepts no body. It creates a random `verification-` key with fixed synthetic fields, confirms matching permanent suppression in Mongo, then enqueues it on the configured DLQ. The response contains `idempotencyKey` and `messageId`; it confirms queuing, not processing. Store rejection or failure never publishes; publication failure leaves safe suppression evidence. Inspect and match the generated key before redriving. The normal consumer then processes it as a terminal duplicate, deletes it and makes no Notify call, with either a null cutover or a boundary before its fixed 2000-01-01 action time. This enables operator verification without recipient mail. It cannot verify successful discard because suppressed evidence is protected. Follow the runbook and retain the native proof gap until it is run against AWS.

### Search retained delivery evidence

Use authenticated `GET /admin/notification-commands/delivery-records` after a queue
copy has disappeared or before a Notify ID exists. Combine explicit UTC
`recordedFromUtc`/`recordedToUtc` bounds (inclusive/exclusive), exact
`notifyNotificationId`, `notificationKey`, `resendOfNotificationKey`, and optional
`outcome`. Bounds use recording time, not business-action time. All retained
outcomes are included by default; pending records remain `delivery-pending`.

The response contains `records` and `nextCursor`. Pages default to 25, with
`pageSize` 1–100, newest recording time first and a stable identifier tie-breaker.
Retain the filters when supplying the opaque cursor for the next page. Concurrent
updates do not provide a snapshot. Summaries expose retained correlation without
email payloads or queue/Notify/evidence-write effects. Acceptance does not establish
recipient delivery. Replacements retain `resendOfNotificationKey` and an encoded
`links.sourceRecord`; inspection links to the source and its replacements.
An empty replacement lookup immediately after publication is not proof of failure.

Use `POST /admin/notification-commands/dlq/resend` with
`{ "selectionToken": "<current-selection>" }` for one accepted command still on
the DLQ. Pending, suppressed, abandoned, conflicting, invalid and unknown states
are refused. There is no batch, request-ID or record-history resend. A's opaque
key always derives the same replacement B, including across fresh selections,
transport copies and hosts. Selecting eligible accepted B later creates its own
replacement. Recipient, template, personalisation, type and original action time
are preserved; cutover and ordinary terminal duplicate protection still apply.

Publication must be confirmed before selected-source deletion. Success returns
`sourceNotificationKey`, `replacementNotificationKey`, `publicationStatus: confirmed`,
`sourceMessageRemoved: true` and encoded `links.sourceRecord` / `links.replacementRecord`.
This confirms queue operations, not Notify acceptance, recipient delivery or an
immediately available B record. After uncertain publication/deletion or a lost
HTTP response, use A's replacement-search link to check retained evidence and
Notify where available. If a successful response was retained, B's record link
also supports lookup. Retry the current selection or wait for visibility and
reinspect A. Every copy of
A still maps to B; an unconfirmed Notify result retains the normal duplicate risk.
Once all A copies disappear, history alone cannot request another replacement.
Never reset A's evidence, override suppression/abandonment or change cutover to
force a resend. Native proof requires the separate
[accepted-source runbook](docs/command-dlq-native-sqs-verification.md#accepted-command-resend).

Search pages contain readable retained summaries: malformed mandatory BSON fields
are excluded before pagination, while unknown string outcomes remain included.
Invalid optional fields return null; dates are not invented and evidence is not
rewritten. An empty exact lookup is not proof that no malformed record exists.
Inspection classifies matching malformed mandatory evidence as unknown and offers
engineering escalation. Search failures log a fixed reason without query or
exception content; caller cancellation is distinct.
See [the HTTP runbook](runbooks/http/README.md) and
[service behaviour](docs/service-behaviour.md#retained-delivery-record-search).

## Code quality and delivery

GitHub Actions runs Consumer tests, validates Compose, builds and scans the
container image, and sends coverage to SonarCloud under
`DEFRA_waste-obligations-notifications`. Dependabot manages NuGet, actions, and
container dependency updates. Journey tests are not currently part of this service.

The isolated Notify fixture creates random synthetic API credentials for each fresh
local volume and reuses them on fixture restarts. Compose supplies them through
an ephemeral volume and fixture bootstrap script. Integration
tests read the same fixture credential from its test-only API. No Notify account
credential is stored in development settings or test source. Compose teardown
removes the generated volume.

## Digest-key lifetime

`EvidenceDigestSecret` is durable identity material, not a replaceable API
credential. Preserve it across hosts and deployments for as long as delivery
evidence or replayable commands exist. Changing it makes old records invisible
to duplicate detection and can resend an already accepted email. The `v1:`
prefix identifies the digest format, not a key ID. The same secret signs DLQ selections and derives Notify references and redrive transport deduplication IDs using separate HMAC domains. Preserve it while any signed selection or replayable command exists. Changing it invalidates selections and changes reference/deduplication identities. Key rotation is unsupported;
it requires a separately designed migration that preserves existing identities.

All publishers and consumers must share the same `RecipientLaneSecret`.
Changing it creates different FIFO groups for the same recipient and can break
ordering while commands remain in the source queue or DLQ. No lane-key rotation
procedure is provided. Keep both values in deployment-owned secret storage.

## Nullable initial cutover and handover

`NotificationCommandDelivery:EmailDeliveryCutoverUtc` defaults to null. With
otherwise valid configuration, processing can run in suppression mode while
Waste Obligations sends every action. MongoDB supplies suppression timestamps;
retries preserve the original evidence. Suppression is durable before queue deletion;
malformed or conflicting commands still retry. The administration APIs cannot clear malformed commands or immutable conflicts. Resolve them through configured queue retention or deployment-owned controlled removal; no verified removal tool is supplied here. Suppressed evidence remains terminal
and is never backfilled into a send after configuration changes. Empty strings,
whitespace, deployment placeholders and non-UTC values are invalid supplied cutovers.

After the producer dry run, configure Notifications with a future X first.
Verify every active host uses X and the post-cutover delivery implementation,
and stop old null-cutover consumers. Then configure Waste Obligations with the
identical X and verify its rollout completes before X. Waste Obligations must
not stop sending while any Notifications consumer still uses null: both paths
would permanently suppress the affected commands. Notifications sends actions at or after
X; Waste Obligations sends only actions before X. The
handover is forward-only after X; do not clear or change the cutover after X.
Each host logs its parsed cutover at startup.
`/health/all` reports `EmailDeliveryCutover` with the normalized UTC value or null,
and fixed suppression/boundary mode. Compare every active host, not only
saved configuration. This diagnostic does not gate `/health`; null is valid.
See [ADR0002](docs/adr/0002-email-delivery-cutover-boundary.md).

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

## Submitted-email verification and local Notify credentials

`DockerSubmittedEmailTests` publishes to an isolated SNS topic and waits for the
Docker consumer to read a WireMock declaration, publish a command and receive a
WireMock Notify acceptance. Its second consumer, queues and database are isolated
from the default Compose consumer. The four hosted scenarios retain detailed
variant and replay assertions.

The isolated integration suite exercises all four direct/scheme and English/Welsh
variants from hosted analytics consumption through the real declaration client,
producer, SQS publisher, command consumer and Mongo claims to the Compose Notify
HTTP fixture. Only the external declaration HTTP response is controlled. The
scenarios verify the original audit recipient, configured template and all five
personalisation fields, acceptance before successful deletion, and terminal replay
suppression. Compose continues to use its generated dummy key and fixture endpoint.

The four `NotifySubmissionPreviewTests` use the genuine GOV.UK Notify preview API;
they render synthetic personalisation without sending emails. They read the
`Submission` template mappings from `src/Consumer/appsettings.json` and process
`Submission__DirectEnglishTemplateId`, `Submission__DirectWelshTemplateId`,
`Submission__SchemeEnglishTemplateId` and `Submission__SchemeWelshTemplateId`
overrides. PR, main and hotfix checks supply the Actions `GOVUKNOTIFY_APIKEY` secret
only to a dedicated preview process. Required previews also apply to Dependabot PRs;
repository administrators must provision a GOV.UK Notify **test-type API key** as
`GOVUKNOTIFY_APIKEY` in both the Actions and Dependabot secret stores. Never use
a live sending key for previews; PR-built tests and dependencies can read this environment value. Missing credentials keep this required gate red;
there is no bot exemption. The PR image build and Trivy scan run before previews,
so missing credentials or provider outages do not prevent those security checks.
Required mode fails on missing credentials,
provider errors or incorrect rendering, and a fresh TRX guard requires all four
cases to execute and pass with zero skips. Publishing depends on that build job.
Normal isolated integration execution in CI has no provider key and explicitly
skips previews; it does not substitute for the required step. The CI report
combines isolated integration results with the dedicated preview results using
test identities, counting each test once. Missing reports remain unavailable,
and failures or unrelated skips remain visible. Preview results also appear
in their own row. Counts come from TRX evidence rather than a fixed case list.

For local previews, create the Git-ignored root `.env` (also excluded from Docker
build inputs) with a placeholder replaced privately:

```dotenv
GovukNotify_ApiKey=replace-with-your-private-notify-key
```

Direct preview execution reads this fallback when `GOVUKNOTIFY_APIKEY` is absent:

```bash
dotnet test --test-modules tests/Consumer.IntegrationTests/bin/Debug/net10.0/Consumer.IntegrationTests.dll --no-build -- --filter-class '*NotifySubmissionPreviewTests'
```

Without a key, local previews report explicit skips. To require provider execution
locally, set `NOTIFY_PREVIEWS_REQUIRED=1`. No credential, rendered email or provider
exception text is written to the preview reports.

For a local consumer using real Notify, the wrapper supplies the same `.env`
fallback independently to `GOVUKNOTIFY_APIKEY` and `Notify__ApiKey`. Explicit
process destination settings take precedence, including an explicitly empty value
and uppercase `NOTIFY__APIKEY`. The wrapper parses only the named value; it never
executes `.env` content or prints the key. It accepts only `run` and launches the
fixed consumer command without forwarding CLI arguments. Direct preview tests read their own `.env` fallback; use the
preview command above instead of this wrapper:

```bash
Notify__BaseAddress=https://api.notifications.service.gov.uk \
  python3 scripts/with-notify-env.py run
```

Set that base address explicitly when using Development settings, which otherwise
point at the isolated fixture. This command is a real Notify runtime configuration;
normal `docker compose up --build -d --wait` remains the isolated fixture setup.
Declaration Read access, effective template access, cutover and both producer
readiness remain deployment prerequisites described above.

Waste Obligations resilience defaults remain ten seconds total, three seconds per
attempt and two transient GET retries. Override inherited settings through
`WasteObligations__Retry__MaxRetryAttempts`, `WasteObligations__Retry__Delay`,
`WasteObligations__AttemptTimeout__Timeout` or
`WasteObligations__TotalRequestTimeout__Timeout`. The analytics processing budget
remains the outer bound; increasing client timeouts does not extend it. The client
uses timeout, retry and attempt timeout strategies without a circuit breaker.
