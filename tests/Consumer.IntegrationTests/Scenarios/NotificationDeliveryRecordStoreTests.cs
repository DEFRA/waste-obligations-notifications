using System.Collections.Concurrent;
using System.Text.Json;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Data.Entities;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class NotificationDeliveryRecordStoreTests : IntegrationTestBase
{
    [Fact]
    public async Task WhenSuppressionIsRetried_ShouldPreserveEvidenceAndRejectConflicts()
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
            Assert.Null(await store.GetSuppression(command, cancellationToken));
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
                await store.GetSuppression(command, cancellationToken)
            );
            Assert.Equal(
                SuppressionClaimResult.Conflict,
                await store.GetSuppression(command with { TemplateId = "changed" }, cancellationToken)
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
            var stored = record.ToJson();
            Assert.DoesNotContain(command.EmailAddress, stored, StringComparison.Ordinal);
            Assert.DoesNotContain(command.IdempotencyKey, stored, StringComparison.Ordinal);
            Assert.DoesNotContain(command.TemplateId, stored, StringComparison.Ordinal);
            Assert.DoesNotContain("private-body", stored, StringComparison.Ordinal);
            await database
                .GetCollection<BsonDocument>("NotificationDeliveryRecord")
                .UpdateOneAsync(
                    new BsonDocument(),
                    Builders<BsonDocument>.Update.Set("outcome", "delivery-pending"),
                    cancellationToken: cancellationToken
                );
            Assert.Null(await store.GetSuppression(command, cancellationToken));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }
}
