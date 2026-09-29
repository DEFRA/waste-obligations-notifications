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
`notificationKey_unique` on `notificationDeliveryRecords` and retains an
existing matching index. Command persistence waits for successful migration
completion; analytics consumption does not depend on migrations.

## Consequences

Migration versions and the lease are stored in the configured notifications
database. Hosts coordinate through the lease and retain it until a cancelled
migration attempt stops. Failures receive bounded retries, and exhausted hosts
leave command writes blocked until restart. Mongo migrations must use the
notifications database; they must not share the Waste Obligations database.
