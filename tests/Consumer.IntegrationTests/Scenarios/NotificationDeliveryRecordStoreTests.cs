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
            Assert.Equal(SuppressionClaimResult.Recorded, await store.RecordSuppression(command, cancellationToken));
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
