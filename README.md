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
and a controlled Notify HTTP fixture. The fixture uses a dummy API key and records
requests only in memory for local integration tests; it does not contact GOV.UK
Notify. Its port is `8086`.

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
API. Enabled command processing validates the API key shape before consuming. Do not put those secrets
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
The delivery claim is separate from migration leases and defaults to 120 seconds.
`ClaimTimeoutSeconds`, `NotifyTimeoutSeconds`, `AcceptanceTimeoutSeconds`, and
`DeleteTimeoutSeconds` initially bound operations to 5, 60, 10, and 5 seconds.
`SafetyHeadroomSeconds` adds 10 seconds. Startup requires the command lease to
cover claim plus send, persistence, deletion and headroom, and visibility to cover
that budget plus the conservative 30-second receive bound: 120 seconds in total.
The receive and claim clocks start before their dependency requests; late
confirmations cannot start a send. Mongo uses its own clock for claim expiry and
owner checks when acceptance is recorded. Failed or indeterminate sends retain
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
subsequently fails. A send failure
metric means the attempt did not confirm acceptance, including timeout or shutdown
cancellation; it does not prove Notify rejected the email. Persistence failures
remain errors in operational logs, and failed claims receive a fixed failure
outcome.

This increment publishes in-process instruments only. CloudWatch EMF export and
its CDP configuration are a separate pending increment; these measurements are
not yet emitted to CDP metrics.

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

## Code quality and delivery

GitHub Actions runs Consumer tests, validates Compose, builds and scans the
container image, and sends coverage to SonarCloud under
`DEFRA_waste-obligations-notifications`. Dependabot manages NuGet, actions, and
container dependency updates. Journey tests are not currently part of this service.

The isolated Notify fixture creates random synthetic API credentials at startup.
Compose supplies them to the consumer through a local ephemeral volume and fixture
bootstrap script; integration
tests read the same fixture credential from its test-only API. No Notify account
credential is stored in development settings or test source. Compose teardown
removes the generated volume.
