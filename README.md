# Waste Obligations Notifications

A CDP consumer service for analytics events and notification commands published by Waste Obligations.

## Scope

The service receives every message from its service-owned SQS subscription to the
`waste_obligations_analytics_events` SNS topic. It logs the analytics event ID and
entity ID, then deletes the successfully processed message. It deliberately does
not send notifications, persist data, or act on the event payload.

The command consumer records pre-cutover commands as `delivery-suppressed` and
deletes them without sending to GOV.UK Notify. Post-cutover delivery belongs to
the next implementation ticket.

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
- Proposed ADRs: [command architecture](docs/adr/0001-notification-command-delivery-architecture.md) and [cutover boundary](docs/adr/0002-email-delivery-cutover-boundary.md).
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
and distinct evidence-digest and recipient-lane secrets. Do not put those secrets
in source control or logs.

When command processing is enabled, Mongo migrations use the same versioned engine and renewable exclusive lease as
Waste Obligations. Migration 001 creates the unique `notificationKey_unique`
index on `NotificationDeliveryRecord`, preserving an existing matching index.
Each host checks migration history and the required unique index before command
consumption starts. A host can become ready after another host applies migrations
without acquiring the lease itself. Commands stay on SQS while migrations are
incomplete; analytics consumption and `/health` continue independently.
Failures are retried up to `MongoMigrations__MaximumAttempts`; after exhaustion,
the host releases the lease and continues checking for completion by another host.
It needs a restart to make further migration attempts itself. Failed attempts,
exhaustion and prolonged readiness waits produce error logs for support alerts.
An attempt timeout requests cancellation and retains the lease until execution
stops; a migration that does not stop needs support intervention.

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
