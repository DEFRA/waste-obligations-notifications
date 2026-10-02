# Waste Obligations Notifications

A CDP consumer service for analytics events and notification commands published by Waste Obligations.

## Scope

The service receives every message from its service-owned SQS subscription to the
`waste_obligations_analytics_events` SNS topic. It logs the analytics event ID and
entity ID, then deletes the successfully processed message. It deliberately does
not send notifications, persist data, or act on the event payload.

The command consumer records pre-cutover commands as `delivery-suppressed` and
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
- ADRs: accepted [command architecture](docs/adr/0001-notification-command-delivery-architecture.md) and proposed [cutover boundary](docs/adr/0002-email-delivery-cutover-boundary.md).
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

`NotificationCommandDelivery` is deployment-owned. Before enabling it, CDP must
provide its FIFO queue URL, cutover timestamp,
and distinct evidence-digest and recipient-lane secrets. Set `Notify__ApiKey` to
the service's Notify API key; `Notify__BaseAddress` defaults to the GOV.UK Notify
API. Enabled command processing validates the SDK's API key shape before consuming. Do not put those secrets
in source control or logs.
Enabled command processing validates the cutover timestamp and both digest
secrets at startup. Blank or deployment-placeholder secrets prevent startup
before commands are consumed. Disabled command processing permits the shipped
deployment placeholders so analytics-only hosts can start. Digest creation also
rejects unconfigured secrets when the publisher is used independently.
Cutover configuration and serialized `actionOccurredAtUtc` values must include
`Z` or a numeric zero offset (`+00:00` or `-00:00`). Offset-free timestamps are
rejected regardless of host timezone; nonzero offsets are rejected without
conversion. Both timestamps are truncated to whole milliseconds before comparison,
matching MongoDB storage precision. Command serialisation and immutable evidence
use the same precision so a Mongo roundtrip does not change command identity.

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

A host-owned exporter starts before the command consumer and observes only that
host's command meter. It uses Waste Obligations' CloudWatch EMF 2.2.0 mechanism,
emitting one SDK JSON document per measurement. SDK platform decoration is skipped;
dimensions remain `Service`, `NotificationType` and, where applicable, `Outcome`.
Configured agent log-group/stream routing is preserved.

EMF configuration uses the same root `AWS_EMF_*` keys as Waste Obligations:

| Setting | Default and behaviour |
| --- | --- |
| `AWS_EMF_ENABLED` | `true`; Development, Compose and isolated tests disable export. |
| `AWS_EMF_NAMESPACE` | Required when enabled; the deployment placeholder is rejected. `Local` permits a missing, empty or whitespace value and uses `Defra.WasteObligationsNotifications`. |
| `AWS_EMF_ENVIRONMENT` | Empty or unknown values use SDK discovery in Lambda, ECS, EC2, then Agent order. Explicit `Local`, `Lambda`, `Agent`, `ECS` or `EC2` selects that SDK environment. |
| `AWS_EMF_AGENT_ENDPOINT` | SDK default `tcp://127.0.0.1:25888`; configure the actual CDP collector endpoint explicitly. |
| `AWS_EMF_AGENT_BUFFER_SIZE` | `100` documents; valid range 1–10000. A full SDK buffer drops new documents; metrics are best effort. |
| `AWS_EMF_SERVICE_NAME`, `AWS_EMF_SERVICE_TYPE` | Optional SDK service/routing settings; service name defaults to `waste-obligations-notifications`. The metric `Service` dimension stays fixed. |
| `AWS_EMF_LOG_GROUP_NAME`, `AWS_EMF_LOG_STREAM_NAME` | Optional agent routing values; they do not become metric dimensions. |
| `AWS_EMF_SHUTDOWN_TIMEOUT_SECONDS` | `5`, with a range of 1–30; bounds the host's wait for SDK sink shutdown. |

Unknown-environment metadata discovery runs once during enabled startup. Each
request is cancellable and bounded to two seconds; late results cannot pass the
eight-second startup confirmation budget. No metadata request runs during command
processing. The AWS SDK owns environment discovery and its process-wide cache,
following Waste Obligations. SDK internal diagnostics are disabled to prevent
private exception or endpoint data entering logs. Exporter startup, serialization
and shutdown failures produce fixed support diagnostics and do not change
command delivery or queue deletion. Export is best effort; the SDK handles agent
transport on its existing background worker. `Local`/`Lambda` use the SDK's
synchronous console sink.

Shutdown detaches observation before asking the sink to stop. The SDK worker has
no cancellation API and may remain active after the bounded wait ends; late
completion cannot restart export or affect another host. Pending metrics may be
lost when the process exits.

Set the namespace, environment and collector routing in CDP separately; local
settings do not configure deployments. For CDP/FluentBit, specify
`AWS_EMF_AGENT_ENDPOINT` rather than relying on `FLUENT_HOST` endpoint derivation:
the pinned SDK's [ECS implementation](https://github.com/awslabs/aws-embedded-metrics-dotnet/blob/v2.2.0/src/Amazon.CloudWatch.EMF/Environment/ECSEnvironment.cs)
builds an invalid derived endpoint. `AWS_EMF_ENVIRONMENT=Agent` also avoids metadata
discovery when the collector route is already known.

When command processing is enabled, Mongo migrations use the same versioned engine and renewable exclusive lease as
Waste Obligations. Migration 001 creates the unique `notificationKey_unique`
index on `NotificationDeliveryRecord`, preserving an existing matching index.
Each host checks migration history and the required unique index before command
consumption starts. A host can become ready after another host applies migrations
without acquiring the lease itself. Commands stay on SQS while migrations are
incomplete; analytics consumption and `/health` continue independently.
Failures are retried up to `MongoMigrations__MaximumAttempts` across all lease
acquisitions on the host. A renewal error cancels the attempt; once the engine
stops, the host releases the lease and can reacquire it using the remaining
attempt budget. After exhaustion,
the host releases the lease and continues checking for completion by another host.
It needs a restart to make further migration attempts itself. Failed attempts,
exhaustion and prolonged readiness waits produce error logs for support alerts.
An attempt timeout or host shutdown requests cancellation and continues renewing
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

`MongoMigrations` configures lease duration, renewal interval, attempt timeout,
retry delay and readiness-wait alert threshold in seconds (the latter uses
`LeaseAcquisitionAlertThresholdSeconds`). The defaults are
60, 15, 300, 30 and 300 respectively, with three attempts. Renewal must be no
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
TLS. Before enabling command processing in CDP, verify authentication, certificate
loading, database permissions, migration completion and `/health/all`. Mongo
migrations and health checks remain conditional on command processing being enabled.

When command processing is enabled, `/health/all` also checks GOV.UK Notify with
one authenticated `GET /v2/templates?type=email`, bounded by the existing
ten-second health timeout. It uses the SDK template-list operation to check
connectivity and credentials. The SDK reads and deserializes the response within
the same cancellation bound; template content is discarded and never exposed or
logged. This does not validate a command's template or confirm email delivery.
Failures expose a fixed description without dependency error details.
Disabled command processing does not register or call this check. `/health`
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
