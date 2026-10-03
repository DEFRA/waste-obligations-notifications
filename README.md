# Waste Obligations Notifications

A CDP consumer service for analytics events and notification commands published by Waste Obligations.

## Scope

The service receives every message from its service-owned SQS subscription to the
`waste_obligations_analytics_events` SNS topic. It logs the analytics event ID and
entity ID, then deletes the successfully processed message. It deliberately does
not send notifications, persist data, or act on the event payload.

With the default null cutover, the command consumer records all valid commands
as `delivery-suppressed` and deletes them for the producer dry run. With a
configured cutover, it records pre-cutover commands as `delivery-suppressed` and
deletes them without sending to GOV.UK Notify. Post-cutover delivery belongs to
ticket 02. Until then, commands at or after the boundary fail without deletion,
retry after visibility timeout, and can reach the DLQ under queue redrive policy.
Operators may need to redrive them after ticket 02 enables delivery. A redriven
command does not regain its original position in the recipient lane.

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
It also creates isolated FIFO command and command dead-letter queues, plus MongoDB.

The Consumer health endpoint is available at `http://localhost:8085/health`.

## Documentation

- [Coding standards](CODING_STANDARDS.md): code structure, style, security, and test conventions.
- [Contributing](CONTRIBUTING.md): formatting, required checks, and change workflow.
- [Service behaviour](docs/service-behaviour.md): message contracts, processing rules, and deployment ownership.
- [Context](CONTEXT.md): notification-delivery terminology.
- ADRs: [command architecture](docs/adr/0001-notification-command-delivery-architecture.md) and accepted [cutover boundary](docs/adr/0002-email-delivery-cutover-boundary.md).
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
provide its FIFO queue URL, optional cutover timestamp,
and distinct evidence-digest and recipient-lane secrets. Do not put those secrets
in source control or logs.
Enabled command processing permits a null cutover and validates any supplied
explicit UTC timestamp and both digest
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

When command processing is enabled, Mongo migrations use the same versioned engine and renewable exclusive lease as
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
TLS. Before enabling command processing in CDP, verify authentication, certificate
loading, database permissions, migration completion and `/health/all`. Mongo
migrations and health checks remain conditional on command processing being enabled.

## Code quality and delivery

GitHub Actions runs Consumer tests, validates Compose, builds and scans the
container image, and sends coverage to SonarCloud under
`DEFRA_waste-obligations-notifications`. Dependabot manages NuGet, actions, and
container dependency updates. Journey tests are not currently part of this service.

## Nullable initial cutover and handover

`NotificationCommandDelivery:EmailDeliveryCutoverUtc` defaults to null. With
otherwise valid configuration, processing can run in suppression mode while
Waste Obligations sends every action. Suppression is durable before queue deletion;
malformed or conflicting commands still retry. Suppressed evidence remains terminal
and is never backfilled into a send after configuration changes. Empty strings,
whitespace, deployment placeholders and non-UTC values are invalid supplied cutovers.

After the producer dry run, configure Notifications with a future X first.
Verify every active host uses X and the post-cutover delivery implementation,
and stop old null-cutover consumers. Then configure Waste Obligations with the
identical X and verify its rollout completes before X. Waste Obligations must
not stop sending while any Notifications consumer still uses null: both paths
would permanently suppress the affected commands. Notifications sends actions at or after
X once ticket02 is present; Waste Obligations sends only actions before X. The
handover is forward-only after X; do not clear the cutover to restore direct sends.
Each host logs its parsed cutover and processing flag at startup.
`/health/all` reports `EmailDeliveryCutover` with the normalized UTC value or null,
processing flag, validity and fixed mode. Compare every active host, not only
saved configuration. This diagnostic does not gate `/health`; null is valid.
See [ADR0002](docs/adr/0002-email-delivery-cutover-boundary.md).

Critical migration completion is reported by the `MongoMigrationCompletion`
entry in `/health/all` and gates `/health` while command processing is enabled.
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
changing the record. Fresh post-cutover commands remain undeleted in this
foundation slice; later Notify delivery owns them.
