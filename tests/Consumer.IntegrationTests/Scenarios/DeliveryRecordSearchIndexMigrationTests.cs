using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Data.Migrations;
using Defra.WasteObligations.Consumer.Utils.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class DeliveryRecordSearchIndexMigrationTests : IntegrationTestBase
{
    private const string RecordedIndexName = "recordedAtUtc_id_desc";
    private const string RecordedAtField = "recordedAtUtc";

    [Fact]
    public async Task WhenSearchMigrationRunsAndRollsBack_ShouldPreserveCriticalAndUnrelatedIndexes()
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_search_indexes_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var records = database.GetCollection<BsonDocument>("NotificationDeliveryRecord");
        var token = TestContext.Current.CancellationToken;
        var completion = new MongoMigrationCompletion();
        var runner = new MongoMigrationRunner(database, NullLogger<MongoMigrationRunner>.Instance, completion);
        var migration = new NotificationDeliveryRecordSearchIndexes();
        var context = new MigrationContext(database, null!, token);
        try
        {
            await runner.Run(token);
            Assert.False(migration.Critical);
            Assert.True(completion.IsCompleted);
            Assert.True(await runner.CheckCompletion(token));
            Assert.True(await migration.ValidateSchema(database, token));
            var indexes = await ListIndexes(records, token);
            Assert.Equal(6, indexes.Count);
            AssertIndex(indexes, RecordedIndexName, new BsonDocument { { RecordedAtField, -1 }, { "_id", -1 } });
            AssertIndex(
                indexes,
                "notifyNotificationId_recordedAtUtc_id_desc",
                new BsonDocument
                {
                    { "notifyNotificationId", 1 },
                    { RecordedAtField, -1 },
                    { "_id", -1 },
                }
            );
            AssertIndex(
                indexes,
                "resendOfNotificationKey_recordedAtUtc_id_desc",
                new BsonDocument
                {
                    { "resendOfNotificationKey", 1 },
                    { RecordedAtField, -1 },
                    { "_id", -1 },
                }
            );
            AssertIndex(
                indexes,
                "outcome_recordedAtUtc_id_desc",
                new BsonDocument
                {
                    { "outcome", 1 },
                    { RecordedAtField, -1 },
                    { "_id", -1 },
                }
            );
            await migration.UpAsync(context);
            await runner.Run(token);
            Assert.Equal(indexes, await ListIndexes(records, token));
            await records.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("notificationType"),
                    new CreateIndexOptions { Name = "unrelated_type" }
                ),
                cancellationToken: token
            );
            await migration.DownAsync(context);
            await migration.DownAsync(context);
            string[] remainingNames = ["_id_", "notificationKey_unique", "unrelated_type"];
            Assert.Equal(remainingNames, (await ListIndexes(records, token)).Select(index => index["name"].AsString));
            Assert.False(await migration.ValidateSchema(database, token));
            Assert.True(await new NotificationDeliveryRecordIndexes().ValidateSchema(database, token));
            Assert.True(completion.IsCompleted);
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("unique")]
    [InlineData("sparse")]
    [InlineData("partial")]
    [InlineData("hidden")]
    [InlineData("collation")]
    public async Task WhenOwnedSearchIndexIsIncompatible_ShouldPreserveItWithoutBlockingCriticalReadiness(
        string variant
    )
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_search_index_conflict_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var records = database.GetCollection<BsonDocument>("NotificationDeliveryRecord");
        var token = TestContext.Current.CancellationToken;
        var migration = new NotificationDeliveryRecordSearchIndexes();
        var context = new MigrationContext(database, null!, token);
        var completion = new MongoMigrationCompletion();
        var runner = new MongoMigrationRunner(database, NullLogger<MongoMigrationRunner>.Instance, completion);
        try
        {
            await new NotificationDeliveryRecordIndexes().UpAsync(context);
            await migration.UpAsync(context);
            await records.Indexes.DropOneAsync(RecordedIndexName, token);
            await records.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    variant == "wrong-key"
                        ? new BsonDocument("wrongField", 1)
                        : new BsonDocument { { RecordedAtField, -1 }, { "_id", -1 } },
                    new CreateIndexOptions<BsonDocument>
                    {
                        Name = RecordedIndexName,
                        Unique = variant == "unique",
                        Sparse = variant == "sparse",
                        Hidden = variant == "hidden",
                        PartialFilterExpression =
                            variant == "partial" ? new BsonDocument("outcome", "delivery-accepted") : null,
                        Collation = variant == "collation" ? new Collation("en") : null,
                    }
                ),
                cancellationToken: token
            );
            var before = await ListIndexes(records, token);
            Assert.False(await migration.ValidateSchema(database, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => migration.UpAsync(context));
            Assert.Equal(before, await ListIndexes(records, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.Run(token));
            Assert.True(completion.IsCompleted);
            Assert.False(await runner.CheckCompletion(token));
            var health = await new MongoMigrationCompletionHealthCheck(completion).CheckHealthAsync(
                new HealthCheckContext(),
                token
            );
            Assert.Equal(HealthStatus.Healthy, health.Status);
            var peerCompletion = new MongoMigrationCompletion();
            var peer = new MongoMigrationRunner(database, NullLogger<MongoMigrationRunner>.Instance, peerCompletion);
            Assert.False(await peer.CheckCompletion(token));
            Assert.True(peerCompletion.IsCompleted);
            var history = await database
                .GetCollection<BsonDocument>("_migrations")
                .Find(new BsonDocument())
                .ToListAsync(token);
            Assert.Equal("1.0.0", Assert.Single(history)["v"].AsString);
            Assert.Equal(before, await ListIndexes(records, token));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

    private static void AssertIndex(IReadOnlyList<BsonDocument> indexes, string name, BsonDocument keys)
    {
        var index = Assert.Single(indexes, index => index["name"] == name);
        Assert.Equal(keys, index["key"]);
        Assert.False(index.GetValue("unique", false).AsBoolean);
        Assert.False(index.GetValue("sparse", false).AsBoolean);
        Assert.False(index.GetValue("hidden", false).AsBoolean);
        Assert.False(index.Contains("partialFilterExpression"));
    }

    private static async Task<List<BsonDocument>> ListIndexes(
        IMongoCollection<BsonDocument> records,
        CancellationToken token
    )
    {
        using var cursor = await records.Indexes.ListAsync(token);

        return (await cursor.ToListAsync(token))
            .OrderBy(index => index["name"].AsString, StringComparer.Ordinal)
            .ToList();
    }
}
