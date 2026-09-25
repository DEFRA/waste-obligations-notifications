# Agents Guidelines

## Coding conventions

- Do not use the `Async` suffix for asynchronous methods.
- Add a blank line before a `return` statement.
- Use constants for values used more than once; inline one-off values.
- Place each production class, interface, record, enum, or struct in its own file named for that type. Keep small test-only helpers with their test when that is clearer.
- Declare variables close to their point of use and use `camelCase` for method-local constants.
- Lint changed C# files with `dotnet csharpier format .`.
- Prefer `x => x.Property` where applicable, collection expressions, and object initializers.
- Specify test variables as `const` where possible. Do not use Arrange/Act/Assert comments.
- Use `_camelCase` for private instance fields.
- Keep test assertions consistent within a test or helper. Avoid adding a shared field solely to satisfy an analyzer when the value belongs locally in one test.
- Put new `appsettings.json` sections at the end of the relevant configuration file.

## Consumer behaviour

- This service consumes every analytics event delivered to its own SQS queue; do not filter by event type or entity type.
- Preserve the producer's transport contract: plain JSON and `Content-Encoding: gzip+base64` messages are supported.
- A successfully parsed event must log its `eventId` and `entityId` before its SQS message is deleted.
- Treat malformed messages, missing required IDs, unsupported encodings, and processing failures as failures: do not delete their SQS message. The service-owned CDP queue redrive policy is responsible for dead-letter routing.
- Keep this service free of notification delivery, business mutations, transformations, and persistence until separately scoped.
- The deployed queue must be a separate SNS subscription from the Waste Obligations producer queue. Never reuse the producer queue.

## Change iterations

- For production code, test code, runtime configuration, dependency, Docker, or Compose changes, run the full local check before committing:
  1. `docker compose up --build -d --wait`
  2. `DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1 dotnet build waste-obligations-notifications.slnx --no-restore -m:1 -nodeReuse:false --disable-build-servers -v:minimal`
  3. `DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1 dotnet test --test-modules tests/Consumer.Tests/bin/Debug/net10.0/Consumer.Tests.dll --no-build -v:minimal`
  4. `DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1 dotnet test --test-modules tests/Consumer.IntegrationTests/bin/Debug/net10.0/Consumer.IntegrationTests.dll --no-build -v:minimal`
  5. `docker compose down -v --remove-orphans`
- Always run the Compose teardown, including after a failed build or test.
- Documentation-only or comment-only changes require `git diff --check`; format changed C# files when applicable.
- Keep unit tests focused on parsing, logging, deletion, and failure behaviour. Use the `Consumer.IntegrationTests` `IntegrationTestBase` and Scenarios folder for real Compose-based health and SNS-to-SQS-to-Consumer wiring.
- Do not alter a shared queue, topic, or deployed CDP configuration from a test. Local Compose may create and own its isolated resources.

## Configuration and delivery

- For every environment variable or configuration change, trace where the Consumer reads it, update local examples, and assess deployed CDP configuration separately. Local Compose values do not configure CDP environments.
- `AnalyticsEventConsumer__QueueUrl` and `AnalyticsEventConsumer__ProcessingEnabled` are deployment-owned settings. Keep credentials out of source control and logs.
- SonarCloud uses project key `DEFRA_waste-obligations-notifications`; do not replace it with the producer service's key.
- Journey tests are not currently part of this repository. Do not add journey-test workflows or secrets unless they are explicitly brought into scope.

## Build guidance

- In the sandbox, avoid plain `dotnet build`; use the build command in the change-iteration checklist.
- If a build is unexpectedly slow, run `dotnet build-server shutdown` and retry.
- The .NET test runner may require sandbox escalation because it creates local IPC endpoints.
