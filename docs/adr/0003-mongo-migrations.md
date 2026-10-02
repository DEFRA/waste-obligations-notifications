# ADR 0003: Versioned Mongo migrations

**Status:** accepted

**Date:** 2026-09-29

## Context

The command record store created its unique notification-key index lazily on its
first write. Storage changes need a versioned process shared by all hosts, and
command idempotency depends on the index existing before persistence begins.
Waste Obligations already has a Mongo migration engine and lease process.

## Decision

Use the same AdaskoTheBeAsT.MongoDbMigrations engine, renewable Mongo lease,
retry policy and attempt timeouts as Waste Obligations. Run migrations in a
background service when command processing is enabled. Migration 001 creates
`notificationKey_unique` on `NotificationDeliveryRecord` and retains an
existing matching index. Each host checks the latest applied migration version
and asks that migration to validate the current schema before receiving commands
from SQS. Migration 001 validates its unique notification-key index. Every new
migration explicitly defines the complete schema checks its application version
needs; the generic runner does not retain obsolete index requirements.

`MongoMigrationCompletion` is a one-time application startup signal. The command
consumer waits once before its first receive. Delivery stores have no dependency
on migration completion and perform no repeated startup waits. This is not an
ongoing schema watchdog: manual index removal after completion is outside this
protection. A host can observe another host's
successful migration without acquiring the lease itself. Analytics consumption
and `/health` do not depend on migrations.

## Consequences

Migration versions and the lease are stored in the configured notifications
database. Hosts coordinate through the lease and retain it until a cancelled
migration attempt stops. Failures receive bounded retries using one host-wide
attempt budget across lease acquisitions. Renewal failures cancel the engine;
after it stops the host may reacquire the lease and use its remaining attempts.
Exhausted hosts
release the lease and continue checking for completion by another host; they
need a restart to execute further migration attempts themselves. Failed attempts,
exhaustion and prolonged readiness waits produce error logs for support alerts.
An attempt timeout or host shutdown cancels the engine while lease renewal
continues while renewal succeeds until execution stops. A migration that does not
stop retains its lease while renewal succeeds and requires support intervention.
Commands remain on SQS until the required
migration is in place.

Confirmation deadlines use a monotonic clock from the start of each acquisition
or renewal request, with half a renewal interval reserved before lease expiry.
The engine's cancellation deadline runs independently of renewal I/O. A late
confirmation cannot revive a cancelled attempt, and renewal checks the existing
lease has not expired using MongoDB's current time. Release and reacquisition wait
for the engine and any outstanding renewal work to stop.

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

Migration completion is reported by the `MongoMigrationCompletion` entry in
`/health/all` while command processing is enabled. Until completion that endpoint
returns 503, while `/health` and analytics remain available. If every host
exhausts its migration attempts, repair the migration problem and restart a host
to retry; hosts continue observing completion by a peer meanwhile. This check
reads the startup signal and performs no schema or index queries.
