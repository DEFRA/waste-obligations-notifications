using System.Collections.Concurrent;
using System.Text.Json;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Data.Entities;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class NotificationDeliveryRecordStoreTests : IntegrationTestBase
{
    [Fact]
    public async Task WhenInspectingDeliveryEvidence_ShouldClassifyRealStateWithoutChangingRecordsOrProjectingNotifyDetails()
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_inspect_state_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var readiness = new MongoMigrationCompletion();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var token = TestContext.Current.CancellationToken;
        var digest = new NotificationCommandDigest(
            Options.Create(
                new NotificationCommandDeliveryOptions
                {
                    QueueUrl = "local",
                    EmailDeliveryCutoverUtc = "2026-10-01T00:00:00Z",
                    EvidenceDigestSecret = "test-inspection-evidence",
                    RecipientLaneSecret = "test-inspection-lane",
                }
            )
        );
        var store = new MongoNotificationDeliveryRecordStore(
            client,
            Options.Create(
                new MongoDbOptions { DatabaseUri = "mongodb://localhost:27017", DatabaseName = databaseName }
            ),
            digest
        );
        var command = new NotificationCommand(
            1,
            "private-inspection-key",
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            "submitted",
            "private-recipient@example.com",
            "private-template",
            JsonSerializer.SerializeToElement(new { body = "private-body" })
        );
        var records = database.GetCollection<BsonDocument>("NotificationDeliveryRecord");
        var filter = new BsonDocument("notificationKey", digest.CreateIdempotencyKeyDigest(command.IdempotencyKey));
        try
        {
            await new MongoMigrationRunner(database, loggerFactory.CreateLogger<MongoMigrationRunner>(), readiness).Run(
                token
            );
            Assert.Equal(new NotificationDeliveryState("unrecorded", null, null), await store.Inspect(command, token));
            Assert.Equal(
                SuppressionClaimResult.Recorded,
                await store.RecordSuppression(command with { IdempotencyKey = "suppressed-inspection-key" }, token)
            );
            Assert.Equal(
                "delivery-suppressed",
                (
                    await store.Inspect(command with { IdempotencyKey = "suppressed-inspection-key" }, token)
                ).Classification
            );
            Assert.Equal(DeliveryClaimResult.Claimed, await store.Claim(command, "inspection-owner", 120, token));
            var activeRecord = await records.Find(filter).SingleAsync(token);
            var active = await store.Inspect(command, token);
            Assert.Equal("active-claim", active.Classification);
            Assert.Equal(new DateTimeOffset(activeRecord["recordedAtUtc"].ToUniversalTime()), active.RecordedAtUtc);
            Assert.Equal(
                new DateTimeOffset(activeRecord["leaseExpiresAtUtc"].ToUniversalTime()),
                active.LeaseExpiresAtUtc
            );
            Assert.Equal(activeRecord, await records.Find(filter).SingleAsync(token));
            Assert.Equal(
                "immutable-conflict",
                (await store.Inspect(command with { TemplateId = "changed-private-template" }, token)).Classification
            );
            await records.UpdateOneAsync(
                filter,
                new BsonDocument("$set", new BsonDocument("leaseExpiresAtUtc", DateTime.UnixEpoch)),
                cancellationToken: token
            );
            Assert.Equal("expired-claim", (await store.Inspect(command, token)).Classification);
            Assert.Equal(DeliveryClaimResult.Claimed, await store.Claim(command, "new-inspection-owner", 120, token));
            Assert.True(
                await store.RecordAcceptance(
                    command,
                    "new-inspection-owner",
                    new NotifyAcceptance(
                        "11111111-1111-1111-1111-111111111111",
                        digest.CreateNotifyReference(command.IdempotencyKey),
                        command.TemplateId,
                        3
                    ),
                    token
                )
            );
            var acceptedRecord = await records.Find(filter).SingleAsync(token);
            var accepted = await store.Inspect(command, token);
            Assert.Equal("delivery-accepted", accepted.Classification);
            Assert.Equal(new DateTimeOffset(acceptedRecord["recordedAtUtc"].ToUniversalTime()), accepted.RecordedAtUtc);
            Assert.Null(accepted.LeaseExpiresAtUtc);
            Assert.Equal(acceptedRecord, await records.Find(filter).SingleAsync(token));
            const string privateOutcome = "private-outcome-recipient@example.com";
            await records.UpdateOneAsync(
                filter,
                new BsonDocument("$set", new BsonDocument("outcome", privateOutcome)),
                cancellationToken: token
            );
            var unknownRecord = await records.Find(filter).SingleAsync(token);
            var unknown = await store.Inspect(command, token);
            Assert.Equal("unknown-delivery-state", unknown.Classification);
            Assert.Equal(new DateTimeOffset(unknownRecord["recordedAtUtc"].ToUniversalTime()), unknown.RecordedAtUtc);
            Assert.Null(unknown.LeaseExpiresAtUtc);
            Assert.Equal(unknownRecord, await records.Find(filter).SingleAsync(token));
            foreach (
                var value in new[]
                {
                    privateOutcome,
                    command.IdempotencyKey,
                    command.EmailAddress,
                    command.TemplateId,
                    "private-body",
                    "11111111-1111-1111-1111-111111111111",
                }
            )
            {
                Assert.DoesNotContain(value, JsonSerializer.Serialize(accepted), StringComparison.Ordinal);
                Assert.DoesNotContain(value, JsonSerializer.Serialize(unknown), StringComparison.Ordinal);
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.DropDatabaseAsync(databaseName, cleanup.Token);
        }
    }

    [Fact]
    public async Task WhenSuppressionIsRetried_ShouldPreserveBaselineCompatibleEvidenceAndRejectConflicts()
    {
        var databaseName = $"notifications_store_test_{Guid.NewGuid():N}";
        var writes = new ConcurrentQueue<BsonDocument>();
        var settings = MongoClientSettings.FromConnectionString("mongodb://localhost:27017");
        settings.ClusterConfigurator = builder =>
            builder.Subscribe<CommandStartedEvent>(started =>
            {
                if (
                    started.DatabaseNamespace.DatabaseName == databaseName
                    && started.CommandName is "insert" or "update"
                    && started.Command[started.CommandName] == "NotificationDeliveryRecord"
                )
                    writes.Enqueue(started.Command.DeepClone().AsBsonDocument);
            });
        using var client = new MongoClient(settings);
        var database = client.GetDatabase(databaseName);
        var readiness = new MongoMigrationCompletion();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var cancellationToken = TestContext.Current.CancellationToken;
        var digest = new NotificationCommandDigest(
            Options.Create(
                new NotificationCommandDeliveryOptions
                {
                    QueueUrl = "local",
                    EmailDeliveryCutoverUtc = "2026-09-29T00:00:00Z",
                    EvidenceDigestSecret = "test-evidence-secret",
                    RecipientLaneSecret = "test-lane-secret",
                }
            )
        );
        var store = new MongoNotificationDeliveryRecordStore(
            client,
            Options.Create(
                new MongoDbOptions { DatabaseUri = "mongodb://localhost:27017", DatabaseName = databaseName }
            ),
            digest
        );
        var command = new NotificationCommand(
            1,
            "private-key",
            new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero).AddTicks(1234567),
            "submitted",
            "recipient@example.com",
            "private-template",
            JsonDocument.Parse("{\"body\":\"private-body\"}").RootElement.Clone()
        );

        try
        {
            await new MongoMigrationRunner(database, loggerFactory.CreateLogger<MongoMigrationRunner>(), readiness).Run(
                cancellationToken
            );
            using var cancelledOperation = new CancellationTokenSource();
            await cancelledOperation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.RecordSuppression(command, cancelledOperation.Token)
            );
            Assert.Equal(
                0,
                await database
                    .GetCollection<BsonDocument>("NotificationDeliveryRecord")
                    .CountDocumentsAsync(new BsonDocument(), cancellationToken: cancellationToken)
            );
            var before = await database.RunCommandAsync<BsonDocument>(
                new BsonDocument("hello", 1),
                cancellationToken: cancellationToken
            );
            var claims = await Task.WhenAll(
                Enumerable.Range(0, 4).Select(_ => store.RecordSuppression(command, cancellationToken))
            );
            Assert.Single(claims, result => result == SuppressionClaimResult.Recorded);
            Assert.Equal(3, claims.Count(result => result == SuppressionClaimResult.TerminalDuplicate));
            var after = await database.RunCommandAsync<BsonDocument>(
                new BsonDocument("hello", 1),
                cancellationToken: cancellationToken
            );
            Assert.NotEmpty(writes);
            Assert.All(
                writes,
                write =>
                {
                    var update = Assert.Single(write["updates"].AsBsonArray).AsBsonDocument["u"].AsBsonDocument;
                    Assert.True(update["$currentDate"]["recordedAtUtc"].AsBoolean);
                    Assert.False(update["$setOnInsert"].AsBsonDocument.Contains("recordedAtUtc"));
                }
            );
            var original = await database
                .GetCollection<BsonDocument>("NotificationDeliveryRecord")
                .Find(new BsonDocument())
                .SingleAsync(cancellationToken);
            Assert.Equal(8, original.ElementCount);
            Assert.Equal(BsonType.DateTime, original["recordedAtUtc"].BsonType);
            Assert.InRange(
                original["recordedAtUtc"].AsBsonDateTime.MillisecondsSinceEpoch,
                before["localTime"].AsBsonDateTime.MillisecondsSinceEpoch,
                after["localTime"].AsBsonDateTime.MillisecondsSinceEpoch
            );
            Assert.Equal(
                SuppressionClaimResult.TerminalDuplicate,
                await store.RecordSuppression(
                    command with
                    {
                        ActionOccurredAtUtc = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(
                            123
                        ),
                    },
                    cancellationToken
                )
            );
            Assert.Equal(
                SuppressionClaimResult.Conflict,
                await store.RecordSuppression(command with { TemplateId = "changed" }, cancellationToken)
            );
            var records = await database
                .GetCollection<NotificationDeliveryRecord>("NotificationDeliveryRecord")
                .Find(Builders<NotificationDeliveryRecord>.Filter.Empty)
                .ToListAsync(cancellationToken);
            var record = Assert.Single(records);
            Assert.Equal(
                original,
                await database
                    .GetCollection<BsonDocument>("NotificationDeliveryRecord")
                    .Find(new BsonDocument())
                    .SingleAsync(cancellationToken)
            );
            Assert.Equal(digest.CreateImmutableFieldsDigest(command), record.ImmutableFields);
            Assert.Equal("delivery-suppressed", record.Outcome);
            Assert.Equal(new DateTime(2026, 9, 28, 0, 0, 0, 123, DateTimeKind.Utc), record.ActionOccurredAtUtc);
            var rawRecord = await database
                .GetCollection<BsonDocument>("NotificationDeliveryRecord")
                .Find(new BsonDocument("notificationKey", digest.CreateIdempotencyKeyDigest(command.IdempotencyKey)))
                .SingleAsync(cancellationToken);
            var baselineRecord = BsonSerializer.Deserialize<BaselineNotificationDeliveryRecord>(rawRecord);
            Assert.Equal(
                new BaselineNotificationDeliveryRecord
                {
                    Id = record.Id,
                    NotificationKey = record.NotificationKey,
                    ImmutableFields = record.ImmutableFields,
                    Recipient = record.Recipient,
                    NotificationType = record.NotificationType,
                    ActionOccurredAtUtc = record.ActionOccurredAtUtc,
                    Outcome = record.Outcome,
                    RecordedAtUtc = record.RecordedAtUtc,
                },
                baselineRecord
            );
            Assert.Equal(
                [
                    "_id",
                    "actionOccurredAtUtc",
                    "immutableFields",
                    "notificationKey",
                    "notificationType",
                    "outcome",
                    "recipient",
                    "recordedAtUtc",
                ],
                rawRecord.Names.Order(StringComparer.Ordinal)
            );
            var stored = rawRecord.ToJson();
            Assert.DoesNotContain(command.EmailAddress, stored, StringComparison.Ordinal);
            Assert.DoesNotContain(command.IdempotencyKey, stored, StringComparison.Ordinal);
            Assert.DoesNotContain(command.TemplateId, stored, StringComparison.Ordinal);
            Assert.DoesNotContain("private-body", stored, StringComparison.Ordinal);
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenThreeHostsCompete_ShouldFenceAcceptanceByCurrentOwnerAfterExpiry(bool reclaim)
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_claim_test_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var readiness = new MongoMigrationCompletion();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var token = TestContext.Current.CancellationToken;
        var digest = new NotificationCommandDigest(
            Options.Create(
                new NotificationCommandDeliveryOptions
                {
                    QueueUrl = "local",
                    EmailDeliveryCutoverUtc = "2026-09-29T00:00:00Z",
                    EvidenceDigestSecret = "test-evidence-secret",
                    RecipientLaneSecret = "test-lane-secret",
                }
            )
        );
        var hosts = Enumerable
            .Range(0, 3)
            .Select(_ => new MongoNotificationDeliveryRecordStore(
                client,
                Options.Create(
                    new MongoDbOptions { DatabaseUri = "mongodb://localhost:27017", DatabaseName = databaseName }
                ),
                digest
            ))
            .ToArray();
        var command = new NotificationCommand(
            1,
            "claim-key",
            new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero),
            "submitted",
            "recipient@example.com",
            "template-1",
            JsonDocument.Parse("{\"body\":\"private-body\"}").RootElement.Clone()
        );
        var records = database.GetCollection<BsonDocument>("NotificationDeliveryRecord");
        try
        {
            await new MongoMigrationRunner(database, loggerFactory.CreateLogger<MongoMigrationRunner>(), readiness).Run(
                token
            );
            var claims = await Task.WhenAll(
                hosts.Select((host, index) => host.Claim(command, $"attempt-{index}", 120, token))
            );
            Assert.Single(claims, result => result == DeliveryClaimResult.Claimed);
            Assert.Equal(2, claims.Count(result => result == DeliveryClaimResult.ActiveClaim));
            var oldOwner = $"attempt-{Array.IndexOf(claims, DeliveryClaimResult.Claimed)}";
            var filter = new BsonDocument("notificationKey", digest.CreateIdempotencyKeyDigest(command.IdempotencyKey));
            var activeRecord = await records.Find(filter).SingleAsync(token);
            Assert.Equal(oldOwner, activeRecord["attemptOwner"].AsString);
            Assert.Equal(BsonType.DateTime, activeRecord["leaseExpiresAtUtc"].BsonType);
            Assert.True(
                activeRecord["leaseExpiresAtUtc"].ToUniversalTime() > activeRecord["recordedAtUtc"].ToUniversalTime()
            );
            Assert.Equal(DeliveryClaimResult.ActiveClaim, await hosts[0].Claim(command, "blocked-attempt", 120, token));
            Assert.Equal(
                DeliveryClaimResult.Conflict,
                await hosts[1].Claim(command with { TemplateId = "changed" }, "conflict-attempt", 120, token)
            );
            Assert.Equal(SuppressionClaimResult.ActiveClaim, await hosts[2].RecordSuppression(command, token));
            await records.UpdateOneAsync(
                filter,
                new BsonDocument("$set", new BsonDocument("leaseExpiresAtUtc", DateTime.UnixEpoch)),
                cancellationToken: token
            );
            var acceptance = new NotifyAcceptance(
                "01234567-89ab-cdef-0123-456789abcdef",
                digest.CreateNotifyReference(command.IdempotencyKey),
                command.TemplateId,
                2
            );
            if (reclaim)
            {
                Assert.Equal(DeliveryClaimResult.Claimed, await hosts[1].Claim(command, "new-attempt", 120, token));
                Assert.False(await hosts[0].RecordAcceptance(command, oldOwner, acceptance, token));
                Assert.True(await hosts[1].RecordAcceptance(command, "new-attempt", acceptance, token));
            }
            else
                Assert.True(await hosts[0].RecordAcceptance(command, oldOwner, acceptance, token));
            Assert.Equal(
                DeliveryClaimResult.TerminalDuplicate,
                await hosts[2].Claim(command, "duplicate-attempt", 120, token)
            );
            var record = await records.Find(filter).SingleAsync(token);
            Assert.Equal("delivery-accepted", record["outcome"].AsString);
            Assert.Equal(acceptance.Reference, record["notifyReference"].AsString);
            Assert.Equal(acceptance.TemplateId, record["templateId"].AsString);
            Assert.Equal(2, record["templateVersion"].AsInt32);
            Assert.Equal(acceptance.NotificationId, record["notifyNotificationId"].AsString);
            Assert.Equal(BsonType.DateTime, record["acceptedAtUtc"].BsonType);
            Assert.Equal(record["recordedAtUtc"], record["acceptedAtUtc"]);
            Assert.False(record.Contains("attemptOwner"));
            Assert.False(record.Contains("leaseExpiresAtUtc"));
            foreach (var privateValue in new[] { command.EmailAddress, command.IdempotencyKey, "private-body" })
                Assert.DoesNotContain(privateValue, record.ToJson(), StringComparison.Ordinal);
            Assert.Equal(
                DeliveryClaimResult.Conflict,
                await hosts[0].Claim(command with { NotificationType = "cancelled" }, "conflict", 120, token)
            );
            Assert.Equal(SuppressionClaimResult.TerminalDuplicate, await hosts[0].RecordSuppression(command, token));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("01234567-89AB-CDEF-0123-456789ABCDEF")]
    [InlineData("{01234567-89AB-CDEF-0123-456789ABCDEF}")]
    public async Task WhenNotifyCanonicalizesTemplateUuid_ShouldPersistAcceptanceAndSuppressExactCommandDuplicate(
        string requestedTemplateId
    )
    {
        const string canonicalTemplateId = "01234567-89ab-cdef-0123-456789abcdef";
        using var client = CreateMongoClient();
        var databaseName = $"notifications_uuid_test_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var readiness = new MongoMigrationCompletion();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var token = TestContext.Current.CancellationToken;
        var digest = new NotificationCommandDigest(
            Options.Create(
                new NotificationCommandDeliveryOptions
                {
                    QueueUrl = "local",
                    EmailDeliveryCutoverUtc = "2026-09-29T00:00:00Z",
                    EvidenceDigestSecret = "test-secret",
                    RecipientLaneSecret = "test-lane",
                }
            )
        );
        var store = new MongoNotificationDeliveryRecordStore(
            client,
            Options.Create(
                new MongoDbOptions { DatabaseUri = "mongodb://localhost:27017", DatabaseName = databaseName }
            ),
            digest
        );
        var command = new NotificationCommand(
            1,
            "uuid-command-key",
            new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero),
            "submitted",
            "recipient@example.com",
            requestedTemplateId,
            JsonDocument.Parse("{}").RootElement.Clone()
        );
        var acceptance = new NotifyAcceptance(
            "01234567-89ab-cdef-0123-456789abcdef",
            digest.CreateNotifyReference(command.IdempotencyKey),
            canonicalTemplateId,
            1
        );
        try
        {
            await new MongoMigrationRunner(database, loggerFactory.CreateLogger<MongoMigrationRunner>(), readiness).Run(
                token
            );
            Assert.Equal(DeliveryClaimResult.Claimed, await store.Claim(command, "attempt", 120, token));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.RecordAcceptance(
                    command,
                    "attempt",
                    acceptance with
                    {
                        TemplateId = "01234567-89ab-cdef-0123-456789abcdee",
                    },
                    token
                )
            );
            Assert.True(await store.RecordAcceptance(command, "attempt", acceptance, token));
            Assert.Equal(DeliveryClaimResult.TerminalDuplicate, await store.Claim(command, "duplicate", 120, token));
            var record = await database
                .GetCollection<BsonDocument>("NotificationDeliveryRecord")
                .Find(new BsonDocument("notificationKey", digest.CreateIdempotencyKeyDigest(command.IdempotencyKey)))
                .SingleAsync(token);
            Assert.Equal("delivery-accepted", record["outcome"].AsString);
            Assert.Equal(canonicalTemplateId, record["templateId"].AsString);
            Assert.Equal(digest.CreateImmutableFieldsDigest(command), record["immutableFields"].AsString);
            Assert.Equal(
                DeliveryClaimResult.Conflict,
                await store.Claim(command with { TemplateId = canonicalTemplateId }, "changed-command", 120, token)
            );
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("delivery-suppressed")]
    [InlineData("delivery-abandoned")]
    public async Task WhenTerminalEvidencePredatesLeaseFields_ShouldRemainTerminal(string outcome)
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_terminal_test_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var readiness = new MongoMigrationCompletion();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var token = TestContext.Current.CancellationToken;
        var digest = new NotificationCommandDigest(
            Options.Create(
                new NotificationCommandDeliveryOptions
                {
                    QueueUrl = "local",
                    EmailDeliveryCutoverUtc = "2026-09-29T00:00:00Z",
                    EvidenceDigestSecret = "test-secret",
                    RecipientLaneSecret = "test-lane",
                }
            )
        );
        var store = new MongoNotificationDeliveryRecordStore(
            client,
            Options.Create(
                new MongoDbOptions { DatabaseUri = "mongodb://localhost:27017", DatabaseName = databaseName }
            ),
            digest
        );
        var command = new NotificationCommand(
            1,
            "terminal-key",
            new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero),
            "submitted",
            "recipient@example.com",
            "template-1",
            JsonDocument.Parse("{}").RootElement.Clone()
        );
        try
        {
            await new MongoMigrationRunner(database, loggerFactory.CreateLogger<MongoMigrationRunner>(), readiness).Run(
                token
            );
            await database
                .GetCollection<BsonDocument>("NotificationDeliveryRecord")
                .InsertOneAsync(
                    new BsonDocument
                    {
                        { "notificationKey", digest.CreateIdempotencyKeyDigest(command.IdempotencyKey) },
                        { "immutableFields", digest.CreateImmutableFieldsDigest(command) },
                        { "recipient", digest.CreateRecipientDigest(command.EmailAddress) },
                        { "notificationType", command.NotificationType },
                        { "actionOccurredAtUtc", command.ActionOccurredAtUtc.UtcDateTime },
                        { "outcome", outcome },
                        { "recordedAtUtc", DateTime.UtcNow },
                    },
                    cancellationToken: token
                );
            Assert.Equal(DeliveryClaimResult.TerminalDuplicate, await store.Claim(command, "attempt", 120, token));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

    // Exact PR 5 record shape: unknown fields must fail rather than hiding a compatibility regression.
    private sealed record BaselineNotificationDeliveryRecord
    {
        [BsonId]
        public ObjectId Id { get; init; }

        public required string NotificationKey { get; init; }

        public required string ImmutableFields { get; init; }

        public required string Recipient { get; init; }

        public required string NotificationType { get; init; }

        public required DateTime ActionOccurredAtUtc { get; init; }

        public required string Outcome { get; init; }

        public required DateTime RecordedAtUtc { get; init; }
    }
}
