# Coding standards

These conventions govern how production code and tests are written. Service
contracts belong in [service behaviour](docs/service-behaviour.md); development
and validation steps belong in [CONTRIBUTING.md](CONTRIBUTING.md).

## C# structure and style

- Target .NET 10. Nullable reference types and warnings-as-errors are enabled;
  do not introduce nullable warnings or suppress analyzers without a reason.
- Put every production class, interface, record, enum, or struct in a file
  named for that type. Small helpers that only serve a test may stay with it.
- Organise code by responsibility. Keep integration boundaries behind interfaces
  and register each responsibility through focused service-collection extensions.
- Prefer sealed concrete types, immutable records for message and options
  models, primary constructors for simple dependency injection, and explicit
  constructors when setup contains logic.
- Use four spaces, LF endings and UTF-8. JSON, project, XML, and YAML files use
  two spaces. Put opening braces on a new line.
- Use `var` where the type is apparent, predefined type keywords, and no
  unnecessary `this.` qualification. Prefer collection expressions, object
  initializers, expression-bodied members, and property lambdas such as
  `x => x.Property` when they improve clarity.
- Use PascalCase for types, members, and constant fields; camelCase for locals,
  parameters, and method-local constants; `_camelCase` for private instance
  fields; and `s_camelCase` for private static fields.
- Do not use an `Async` suffix. Pass `CancellationToken` through asynchronous
  work and I/O. Add a blank line before each `return` statement.
- Declare a variable close to where it is used. Use constants for values that
  share a meaning across uses; inline one-off values. Keep related conditions
  together when that remains clear.

CSharpier owns C# formatting; `.editorconfig` defines editor settings and naming
preferences. See [CONTRIBUTING.md](CONTRIBUTING.md) for formatting and checks.

## Validation, configuration, and security

- Validate external input at the boundary before acting on it.
- Pass cancellation through asynchronous work; distinguish cancellation from
  processing failures.
- Use structured logging for identifiers and outcomes. Never log credentials,
  digest material, email addresses, personalisation, message bodies, rendered
  content, or full third-party responses.
- Use a typed options record with a section name, data annotations,
  `ValidateDataAnnotations()`, and `ValidateOnStart()` for configuration.
- Keep credentials out of source control. Keep deployed settings as placeholders
  and local Compose values in development settings.
- Add health checks for deployed dependencies. Keep routine health checks light
  and avoid logging them at information level.

## Mongo entities and conventions

Keep persisted entities in `Consumer/Data/Entities`, under the matching namespace.
Register the Waste Obligations Mongo conventions before mapping any entities:
camel-case element names and string enum representations. Prefer these conventions
to per-property BSON attributes unless a field needs a different storage contract.

Use the entity type name for entity collections, for example
`NotificationDeliveryRecord`. Match Waste Obligations for supporting collections:
use underscore-separated names with a leading underscore, such as
`_migrations_lease`. The migration engine stores its history in `_migrations`.

## Tests

- Use xUnit v3 and the existing assertion style in the surrounding test. Do
  not add Arrange/Act/Assert comments.
- Name tests around the condition and expected outcome. Keep test values
  `const` where possible, keep assertion style consistent, and do not introduce
  a shared field solely to satisfy an analyzer.
- Keep unit tests focused on one behaviour. Use NSubstitute and local helpers
  where that keeps a test self-contained.
- Keep integration tests focused on real dependency wiring; use unit tests for
  detailed parsing and formatting checks. See [CONTRIBUTING.md](CONTRIBUTING.md)
  for the integration-test structure.
- Test that sensitive data is excluded from persistence and logs. Do not alter
  shared queues, topics, or deployed configuration from a test.

