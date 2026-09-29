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

## Test

```bash
dotnet build tests/Consumer.Tests/Consumer.Tests.csproj
dotnet test --test-modules tests/Consumer.Tests/bin/Debug/net10.0/Consumer.Tests.dll --no-build

docker compose up --build -d --wait
dotnet build tests/Consumer.IntegrationTests/Consumer.IntegrationTests.csproj
dotnet test --test-modules tests/Consumer.IntegrationTests/bin/Debug/net10.0/Consumer.IntegrationTests.dll --no-build
docker compose down -v --remove-orphans
```

## Configuration

`AnalyticsEventConsumer` is disabled by default. CDP deployment configuration must
set `AnalyticsEventConsumer__ProcessingEnabled` to `true` and provide the
service-owned `AnalyticsEventConsumer__QueueUrl`. The deployed queue must be a
separate subscription from the producer queue and must have the CDP dead-letter
queue convention configured.

`NotificationCommandDelivery` is deployment-owned. Before enabling it, CDP must
provide its FIFO queue URL, MongoDB connection and database, cutover timestamp,
and distinct evidence-digest and recipient-lane secrets. Do not put those secrets
in source control or logs.

## Code quality and delivery

GitHub Actions runs Consumer tests, validates Compose, builds and scans the
container image, and sends coverage to SonarCloud under
`DEFRA_waste-obligations-notifications`. Dependabot manages NuGet, actions, and
container dependency updates. Journey tests are not currently part of this service.
