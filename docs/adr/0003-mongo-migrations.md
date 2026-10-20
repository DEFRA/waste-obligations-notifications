# ADR 0003: Versioned Mongo migrations

**Status:** accepted

**Date:** 2026-09-29

## Context

The command record store created its unique notification-key index lazily on its
first write. Storage changes need a versioned process shared by all hosts, and
command idempotency depends on the index existing before persistence begins.
Waste Obligations already has a Mongo migration engine and lease process.

## Decision

Use the AdaskoTheBeAsT.MongoDbMigrations engine used by Waste Obligations, with a
renewable Mongo lease and the retry and timeout policies below. Run migrations in a
background service on every host. Migration 001 creates
`notificationKey_unique` on `NotificationDeliveryRecord` and retains an
existing matching index.
A critical index must have completed building, not merely appear in the catalog.
Incompatible existing definitions are rejected without dropping them. A retry
confirms an unfinished matching build before saving migration history; completed
hidden indexes remain valid. Migration 001 is explicitly critical. Migration flags
are non-critical by default: optional performance changes do not determine
whether a host can enter service. The runner establishes critical prerequisites
before continuing later optional work under the same lease. Each critical
version must have applied history; the latest critical migration validates the
current schema so future critical changes can replace earlier index requirements.

`MongoMigrationCompletion` records critical startup readiness once. `/health`
returns 503 until it is established. After its first successful response completes,
the shared application-startup boundary releases analytics and command consumers.
HTTP starts before this boundary so CDP can probe it. Migration work, logging and
metrics start before readiness; stores and business operations have no migration
checks. Extended or authorised health requests do not release consumers. A peer
can establish the prerequisite without this host acquiring the lease. This
replaces the former independent-analytics/liveness startup contract.

## Consequences

Migration versions and the lease are stored in the configured notifications
database. Hosts coordinate through a renewable exclusive lease. The
[migration service](../../src/Consumer/Data/MongoMigrationService.cs),
[lease service](../../src/Consumer/Data/MongoMigrationLeaseService.cs),
[runner](../../src/Consumer/Data/MongoMigrationRunner.cs) and
[options](../../src/Consumer/Data/MongoMigrationOptions.cs) define attempt,
renewal and operation budgets. Preserve ownership until cancelled engine and
renewal work stop; late confirmations must not revive cancelled work. Exhausted
hosts can observe another host's completion but need restart to attempt work
again. Failed attempts and prolonged readiness waits require support diagnosis.

This bounds cancellation requests, not the duration of cancellation-resistant
engine operations. The engine performs some synchronous metadata operations, and
the lease has no fencing; after genuine lease loss those operations can overlap
another host. The acquisition protocol also retains its existing reliance on
host-clock alignment for stored expiry values.
Mongo migrations must use the notifications database; they must not share the
Waste Obligations database.

Mongo connection configuration lives in the `Mongo` section, using `DatabaseUri`
and `DatabaseName` as in Waste Obligations. Notifications uses its own database
and migration history. The client registers AWS authentication for CDP task-role
credentials and uses primary reads so duplicate-command comparisons do not depend
on secondary replication lag.

Entity collection names use the entity type name (`NotificationDeliveryRecord`).
Supporting collections retain the Waste Obligations underscore convention:
`_migrations_lease` for the exclusive lease and `_migrations` for migration history.
If a database already contains `notificationDeliveryRecords`, rename that
collection with all command-processing hosts stopped before deploying this
naming change. This change does not rename stored collections automatically.

Persisted entities live in `Consumer/Data/Entities`. Mongo uses the same global
camel-case element and string-enum conventions as Waste Obligations, registered
before entity mapping. Lease documents therefore use `owner` and `expiresAt`;
existing documents using `Owner` and `ExpiresAt` must be reconciled with all
command-processing hosts stopped before rollout.

CDP owns replacement of hosts that remain unhealthy. Long critical migrations
must fit the actual platform startup budget or be applied before rollout; a
configured operation timeout is not a deployment deadline. Extended health
reports later dependency outages without changing the latched startup result.
The [migration timing scenarios](../../tests/Consumer.IntegrationTests/Scenarios/MongoMigrationTimingTests.cs)
and [index completion scenarios](../../tests/Consumer.IntegrationTests/Scenarios/MongoIndexCompletionTests.cs)
provide executable readiness and cancellation examples.

## Operation deadline amendment

Critical operations have a 20-second cooperative deadline; standard operations
retain 300 seconds. The actual engine hooks arm a separate deadline for each
operation. A whole-chain timeout must not truncate an intervening standard
migration or report readiness before a later critical prerequisite. Cancellation
requests do not release ownership until engine and renewal work have stopped.
