# Contributing

This document covers development workflow, validation, and documentation updates.
Follow [CODING_STANDARDS.md](CODING_STANDARDS.md) when writing code.

## Formatting and test structure

- Run `dotnet csharpier format .` after changing C#.
- Put real Compose-backed wiring tests in `Consumer.IntegrationTests/Scenarios`
  and inherit `IntegrationTestBase`.
- Local Compose may create and own isolated resources. Tests must not alter
  shared queues, topics, or deployed CDP configuration.
- Journey tests are not currently part of this repository. Do not add their
  workflows or secrets unless explicitly brought into scope.

## Configuration changes

For every configuration or environment-variable change, trace its options binding
and use, update local examples, and assess deployed CDP configuration separately.
Local Compose values do not configure CDP environments. Add new `appsettings.json`
sections at the end of the relevant file.

## Required checks

For production or test code, runtime configuration, dependencies, Docker, or
Compose changes, run the full local check. Always tear Compose down, including
after a failed build or test:

```bash
docker compose up --build -d --wait
DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1 dotnet build waste-obligations-notifications.slnx --no-restore -m:1 -nodeReuse:false --disable-build-servers -v:minimal
DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1 dotnet test --test-modules tests/Consumer.Tests/bin/Debug/net10.0/Consumer.Tests.dll --no-build -v:minimal
DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1 dotnet test --test-modules tests/Consumer.IntegrationTests/bin/Debug/net10.0/Consumer.IntegrationTests.dll --no-build -v:minimal
docker compose down -v --remove-orphans
```

For documentation-only or comment-only changes, run `git diff --check`.

Update the README when scope, local operation, configuration, or delivery
behaviour changes. Add or update an ADR for a durable decision about message
contracts, idempotency, ordering, privacy, storage, or the cutover boundary.
Use the terms in [CONTEXT.md](CONTEXT.md) consistently. Keep current service
contracts in [service behaviour](docs/service-behaviour.md) and coding conventions
in [CODING_STANDARDS.md](CODING_STANDARDS.md).
