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
- When adding, changing or removing a mapped admin or health endpoint, update
  `runbooks/http` with its method, path and required authentication. Keep request
  examples and recovery guidance aligned with the endpoint contract. The script
  tests check route parity using the current literal route-mapping conventions;
  update that check when introducing a different mapping convention.

## Configuration changes

For every configuration or environment-variable change, trace its options binding
and use, update local examples, and assess deployed CDP configuration separately.
Local Compose values do not configure CDP environments. Add new `appsettings.json`
sections at the end of the relevant file.

Document deployment order when new startup validation affects an existing enabled
feature. Provision its configuration and access first, or disable it before the
new image deploys; a host startup failure can also stop unrelated consumers and
administration. See [deployment ownership](docs/service-behaviour.md#deployment-ownership).

Treat configuration used in immutable command evidence as part of replay identity.
Finalize template mappings before dry-run suppression and assess conflicts from
later changes while commands remain replayable. Do not present a configuration
override as an unrestricted rollout change.

## Required checks

For production or test code, runtime configuration, dependencies, Docker, or
Compose changes, run the full local check. Always tear Compose down, including
after a failed build or test:

```bash
python3 -m unittest discover -s tests/Script.Tests
docker compose up --build -d --wait
DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1 dotnet build waste-obligations-notifications.slnx --no-restore -m:1 -nodeReuse:false --disable-build-servers -v:minimal
DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1 dotnet test --test-modules tests/Consumer.Tests/bin/Debug/net10.0/Consumer.Tests.dll --no-build -v:minimal
DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1 dotnet test --test-modules tests/Consumer.IntegrationTests/bin/Debug/net10.0/Consumer.IntegrationTests.dll --no-build -v:minimal
docker compose down -v --remove-orphans
```

For documentation-only or comment-only changes, run `git diff --check`.

A change to scope, local operation, configuration or delivery behaviour requires a
documentation assessment, not necessarily additional prose. Update implementation
and test links where needed. Add prose only for external contracts, operational
requirements or decision rationale that cannot be established reliably from code
and tests. Follow the documentation guidance in [AGENTS.md](AGENTS.md).

Update the README when its overview, links or operational guidance need to change.
Add or update an ADR when a durable decision or its rationale changes, including
message contracts, idempotency, ordering, privacy, storage or the cutover boundary.
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

Keep required image builds and security scans ahead of external provider checks,
so missing provider credentials or outages cannot skip those checks. Preserve
report uploads on failure. Required Notify previews use test-type API keys in both
Actions and Dependabot secret stores; never supply live sending keys to PR-built
tests or dependencies. A missing key must fail the preview gate, not exempt bots.

Run actionlint v1.7.12 with ShellCheck available on `PATH` when changing workflows.
Actionlint skips shell analysis when ShellCheck is missing, so an actionlint-only
pass does not reproduce CI validation. CI verifies its release
archive against a pinned SHA256 checksum before running it. Review action commits,
container digests, and the actionlint version and checksum manually when updating
these pins.
