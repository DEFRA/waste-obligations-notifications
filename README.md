# Waste Obligations Notifications

A CDP consumer service for analytics events and notification commands published by Waste Obligations.

## Scope

The service receives every message from its service-owned SQS subscription to the
`waste_obligations_analytics_events` SNS topic. It logs the analytics event ID and
entity ID, then deletes the successfully processed message. It deliberately does
not send notifications, persist data, or act on the event payload.

With the default null cutover, the command consumer durably suppresses every
valid command and deletes it without Notify. With a supplied boundary, it
records pre-cutover commands as `delivery-suppressed` and
sends at-or-after-cutover commands through GOV.UK Notify. It acquires a Mongo
claim, makes one send request, records acceptance, and then deletes the command.
Matching accepted, suppressed, or abandoned commands are deleted without another
send. Conflicts, active claims, failed sends and incomplete persistence remain on
SQS for visibility-timeout retry and queue redrive. A redriven command does not
regain its original recipient-lane position.

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

New suppression records retain their original eight-field shape, omitting absent
lease and Notify fields. Claims and accepted records retain their additional
evidence; this change does not rewrite existing documents.

The idempotency key is used unchanged as the FIFO deduplication ID. Publishing
and consumption reject keys longer than 128 characters or containing whitespace,
control characters, or characters outside the ASCII letters, digits and punctuation
allowed by [SQS SendMessage](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessage.html#API_SendMessage_RequestParameters).

Malformed messages, messages without `eventId` or `entityId`, and unsupported
content encodings are not deleted. The service-owned SQS queue's CDP redrive
configuration routes them to its convention-led dead-letter queue after the
configured receive attempts are exhausted.

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
- [Agent guidelines](AGENTS.md): entry points and sandbox build guidance for coding agents.

## Test

See [Contributing](CONTRIBUTING.md#required-checks) for the required local build,
unit tests, integration tests, and Compose teardown.

## Configuration

`AnalyticsEventConsumer` is disabled by default. CDP deployment configuration must
set `AnalyticsEventConsumer__ProcessingEnabled` to `true` and provide the
service-owned `AnalyticsEventConsumer__QueueUrl`. The deployed queue must be a
separate subscription from the producer queue and must have the CDP dead-letter
queue convention configured.

`NotificationCommandDelivery` is deployment-owned. Every host consumes commands
after successful startup readiness; there is no command-processing enablement
flag. Before deployment, provide the FIFO queue URL, optional cutover, distinct
evidence-digest and recipient-lane secrets, and Mongo connectivity/permissions, and Notify credentials.
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
command settings leave analytics polling and the shared SQS client unchanged.
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
prefix identifies the digest format, not a key ID. Key rotation is unsupported;
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
malformed or conflicting commands still retry. Suppressed evidence remains terminal
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
