using System.Text.Json;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Data.Entities;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class NotificationDeliveryRecordStoreTests : IntegrationTestBase
{
    [Fact]
    public async Task WhenSuppressionIsRetried_ShouldPreserveEvidenceAndRejectConflicts()
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_store_test_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var readiness = new MongoMigrationReadiness();
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
            digest,
            readiness
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
            using var beforeMigration = new CancellationTokenSource();
            await beforeMigration.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.RecordSuppression(command, beforeMigration.Token)
            );
            await new MongoMigrationRunner(database, loggerFactory.CreateLogger<MongoMigrationRunner>(), readiness).Run(
                cancellationToken
            );
            Assert.Equal(SuppressionClaimResult.Recorded, await store.RecordSuppression(command, cancellationToken));
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
            Assert.Equal(digest.CreateImmutableFieldsDigest(command), record.ImmutableFields);
            Assert.Equal("delivery-suppressed", record.Outcome);
            Assert.Equal(new DateTime(2026, 9, 28, 0, 0, 0, 123, DateTimeKind.Utc), record.ActionOccurredAtUtc);
            var stored = record.ToJson();
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

    [Fact]
    public async Task WhenThreeHostsCompete_ShouldExcludeActiveOwnersRecoverExpiryAndRejectStaleAcceptance()
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_claim_test_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var readiness = new MongoMigrationReadiness();
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
                digest,
                readiness
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
            Assert.Equal(DeliveryClaimResult.ActiveClaim, await hosts[0].Claim(command, "blocked-attempt", 120, token));
            Assert.Equal(
                DeliveryClaimResult.Conflict,
                await hosts[1].Claim(command with { TemplateId = "changed" }, "conflict-attempt", 120, token)
            );
            Assert.Equal(SuppressionClaimResult.ActiveClaim, await hosts[2].RecordSuppression(command, token));
            var filter = new BsonDocument("notificationKey", digest.CreateIdempotencyKeyDigest(command.IdempotencyKey));
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
            Assert.False(await hosts[0].RecordAcceptance(command, oldOwner, acceptance, token));
            Assert.Equal(DeliveryClaimResult.Claimed, await hosts[1].Claim(command, "new-attempt", 120, token));
            Assert.False(await hosts[0].RecordAcceptance(command, oldOwner, acceptance, token));
            Assert.True(await hosts[1].RecordAcceptance(command, "new-attempt", acceptance, token));
            Assert.Equal(
                DeliveryClaimResult.TerminalDuplicate,
                await hosts[2].Claim(command, "duplicate-attempt", 120, token)
            );
            var record = await records.Find(filter).SingleAsync(token);
            Assert.Equal("delivery-accepted", record["outcome"].AsString);
            Assert.Equal(2, record["templateVersion"].AsInt32);
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
        var readiness = new MongoMigrationReadiness();
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
            digest,
            readiness
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
        var readiness = new MongoMigrationReadiness();
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
            digest,
            readiness
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
}
