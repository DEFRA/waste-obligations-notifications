using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Data.Migrations;
using Defra.WasteObligations.Consumer.Utils.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class DeliveryRecordParentSearchIndexMigrationTests : IntegrationTestBase
{
    private const string IndexName = "resendOfNotificationKey_recordedAtUtc_id_desc";

    [Fact]
    public async Task WhenParentSearchMigrationRunsAndRollsBack_ShouldPreserveOtherIndexes()
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_parent_indexes_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var records = database.GetCollection<BsonDocument>("NotificationDeliveryRecord");
        var token = TestContext.Current.CancellationToken;
        var migration = new NotificationDeliveryRecordParentSearchIndex();
        var context = new MigrationContext(database, null!, token);
        try
        {
            await new NotificationDeliveryRecordIndexes().UpAsync(context);
            await new NotificationDeliveryRecordSearchIndexes().UpAsync(context);
            await records.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("notificationType"),
                    new CreateIndexOptions { Name = "unrelated_type" }
                ),
                cancellationToken: token
            );
            var before = await ListIndexes(records, token);

            await migration.UpAsync(context);

            Assert.False(migration.Critical);
            Assert.Equal("3.0.0", migration.Version.ToString());
            Assert.True(await migration.ValidateSchema(database, token));
            var indexes = await ListIndexes(records, token);
            Assert.Equal(before.Count + 1, indexes.Count);
            var index = Assert.Single(indexes, index => index["name"] == IndexName);
            Assert.Equal(
                new BsonDocument
                {
                    { "resendOfNotificationKey", 1 },
                    { "recordedAtUtc", -1 },
                    { "_id", -1 },
                },
                index["key"]
            );
            Assert.Equal(
                new BsonDocument("resendOfNotificationKey", new BsonDocument("$type", "string")),
                index["partialFilterExpression"]
            );
            Assert.False(index.GetValue("unique", false).AsBoolean);
            Assert.False(index.GetValue("sparse", false).AsBoolean);
            Assert.False(index.GetValue("hidden", false).AsBoolean);
            Assert.Equal(
                new BsonDocument("locale", "simple"),
                index.GetValue("collation", new BsonDocument("locale", "simple"))
            );

            await migration.UpAsync(context);

            Assert.Equal(indexes, await ListIndexes(records, token));

            await migration.DownAsync(context);
            await migration.DownAsync(context);

            Assert.Equal(before, await ListIndexes(records, token));
            Assert.False(await migration.ValidateSchema(database, token));
            Assert.True(await new NotificationDeliveryRecordIndexes().ValidateSchema(database, token));
            Assert.True(await new NotificationDeliveryRecordSearchIndexes().ValidateSchema(database, token));
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
    [InlineData("missing-partial")]
    [InlineData("wrong-partial")]
    [InlineData("hidden")]
    [InlineData("collation")]
    public async Task WhenParentSearchIndexConflicts_ShouldPreserveItWithoutBlockingCriticalReadiness(string variant)
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_parent_conflict_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var records = database.GetCollection<BsonDocument>("NotificationDeliveryRecord");
        var token = TestContext.Current.CancellationToken;
        var migration = new NotificationDeliveryRecordParentSearchIndex();
        var context = new MigrationContext(database, null!, token);
        var completion = new MongoMigrationCompletion();
        var runner = new MongoMigrationRunner(database, NullLogger<MongoMigrationRunner>.Instance, completion);
        try
        {
            await new NotificationDeliveryRecordIndexes().UpAsync(context);
            await new NotificationDeliveryRecordSearchIndexes().UpAsync(context);
            await records.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    variant == "wrong-key"
                        ? new BsonDocument("wrongField", 1)
                        : new BsonDocument
                        {
                            { "resendOfNotificationKey", 1 },
                            { "recordedAtUtc", -1 },
                            { "_id", -1 },
                        },
                    new CreateIndexOptions<BsonDocument>
                    {
                        Name = IndexName,
                        Unique = variant == "unique",
                        Sparse = variant == "sparse",
                        Hidden = variant == "hidden",
                        PartialFilterExpression = variant switch
                        {
                            "sparse" or "missing-partial" => null,
                            "wrong-partial" => new BsonDocument(
                                "resendOfNotificationKey",
                                new BsonDocument("$exists", true)
                            ),
                            _ => new BsonDocument("resendOfNotificationKey", new BsonDocument("$type", "string")),
                        },
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
            Assert.Equal(["1.0.0", "2.0.0"], history.Select(item => item["v"].AsString).Order(StringComparer.Ordinal));
            Assert.Equal(before, await ListIndexes(records, token));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
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
