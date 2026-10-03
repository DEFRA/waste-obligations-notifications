# Agent guidelines

## Repository guidance

- Follow [CODING_STANDARDS.md](CODING_STANDARDS.md) for code and test conventions.
- Follow [CONTRIBUTING.md](CONTRIBUTING.md) for formatting, required checks,
  configuration changes, and documentation updates. Run the full local check
  before committing code, configuration, dependency, Docker, or Compose changes;
  always tear Compose down, including after failures.
- Preserve the contracts in [service behaviour](docs/service-behaviour.md).
  Analytics consumption and notification-command processing have separate scopes.
- Use [CONTEXT.md](CONTEXT.md) for terminology. Read the proposed
  [command architecture](docs/adr/0001-notification-command-delivery-architecture.md)
  and [cutover ADR](docs/adr/0002-email-delivery-cutover-boundary.md) when working
  on notification commands; check current scope in the service-behaviour document.
- Never alter shared queues, topics, or deployed CDP configuration from a test.
  Local Compose may create and own its isolated resources.
- Keep credentials out of source control and logs.

## Build guidance for agents

- In the sandbox, avoid plain `dotnet build`; use the build command in
  [CONTRIBUTING.md](CONTRIBUTING.md).
- If a build is unexpectedly slow, run `dotnet build-server shutdown` and retry.
- The .NET test runner may require sandbox escalation because it creates local
  IPC endpoints.
