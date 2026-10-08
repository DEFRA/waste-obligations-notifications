# Contributing

This document covers development workflow, validation, and documentation updates.
Follow [CODING_STANDARDS.md](CODING_STANDARDS.md) when writing code.

## Formatting and test structure

- Run `dotnet csharpier format .` after changing C#.
- Put real Compose-backed wiring tests in `Consumer.IntegrationTests/Scenarios`
  and inherit `IntegrationTestBase`.
- Local Compose may create and own isolated resources. Tests must not alter
  shared queues, topics, or deployed CDP configuration.
- The isolated Compose Mongo enables test commands for index-build failpoint
  regressions. These tests run serially, release failpoints before cleanup, and
  must never run against a shared or deployed Mongo instance.
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

## CI security checks

Pull request validation and SonarCloud wait for workflow validation and dependency
security review. The existing required `Run Pull Request Checks` check explicitly
fails if either prerequisite fails, so a skipped job cannot satisfy the branch
rule. Dependency review blocks High and Critical vulnerabilities in
runtime, development, and unknown dependency scopes. The .NET restore audits all
NuGet dependencies; audit warnings remain errors.

Trivy scans the built image for High and Critical vulnerabilities and secrets,
including vulnerabilities without an available fix. Findings and scanner failures
fail validation. The scanner reads an exported image archive without access to the
Docker socket. CI reports show blocking findings without matched secret content.

Run actionlint v1.7.12 locally when changing workflows. CI verifies its release
archive against a pinned SHA256 checksum before running it. Review action commits,
container digests, and the actionlint version and checksum manually when updating
these pins.
