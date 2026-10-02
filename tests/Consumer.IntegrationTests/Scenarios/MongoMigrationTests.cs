using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Data.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class MongoMigrationTests : IntegrationTestBase
{
    [Fact]
    public async Task WhenAnotherHostAppliesMigration_ShouldCompleteReadinessWithoutRunningMigration()
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_readiness_test_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var cancellationToken = TestContext.Current.CancellationToken;
        var waitingReadiness = new MongoMigrationCompletion();
        var waitingRunner = new MongoMigrationRunner(
            database,
            NullLogger<MongoMigrationRunner>.Instance,
            waitingReadiness
        );
        var waiting = waitingReadiness.Wait(cancellationToken);
        var migratingRunner = new MongoMigrationRunner(
            database,
            NullLogger<MongoMigrationRunner>.Instance,
            new MongoMigrationCompletion()
        );

        try
        {
            Assert.False(await waitingRunner.CheckReadiness(cancellationToken));
            var migration = new NotificationDeliveryRecordIndexes();
            await migration.UpAsync(new MigrationContext(database, null!, cancellationToken));
            Assert.False(await waitingRunner.CheckReadiness(cancellationToken));
            Assert.False(waiting.IsCompleted);

            await migratingRunner.Run(cancellationToken);

            Assert.True(await waitingRunner.CheckReadiness(cancellationToken));
            await waiting;
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-key")]
    [InlineData("non-unique")]
    public async Task WhenMigrationHistoryExistsWithoutRequiredIndex_ShouldNotCompleteReadiness(string invalidIndex)
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_index_test_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var cancellationToken = TestContext.Current.CancellationToken;
        var migratingRunner = new MongoMigrationRunner(
            database,
            NullLogger<MongoMigrationRunner>.Instance,
            new MongoMigrationCompletion()
        );

        try
        {
            await migratingRunner.Run(cancellationToken);
            var records = database.GetCollection<BsonDocument>("NotificationDeliveryRecord");
            await records.Indexes.DropOneAsync("notificationKey_unique", cancellationToken);
            if (invalidIndex != "missing")
            {
                await records.Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys.Ascending(
                            invalidIndex == "wrong-key" ? "wrongField" : "notificationKey"
                        ),
                        new CreateIndexOptions
                        {
                            Name = "notificationKey_unique",
                            Unique = invalidIndex != "non-unique",
                        }
                    ),
                    cancellationToken: cancellationToken
                );
            }
            var readiness = new MongoMigrationCompletion();
            var runner = new MongoMigrationRunner(database, NullLogger<MongoMigrationRunner>.Instance, readiness);

            Assert.False(await runner.CheckReadiness(cancellationToken));
            Assert.False(readiness.Wait(cancellationToken).IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.Run(cancellationToken));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenAnotherHostOwnsLease_ShouldAcquireOnlyAfterRelease()
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_lease_test_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var firstHost = new MongoMigrationLeaseService(database, TimeProvider.System);
        var secondHost = new MongoMigrationLeaseService(database, TimeProvider.System);
        var cancellationToken = TestContext.Current.CancellationToken;
        var duration = TimeSpan.FromSeconds(60);

        try
        {
            Assert.True(await firstHost.TryAcquire(duration, cancellationToken));
            Assert.False(await secondHost.TryAcquire(duration, cancellationToken));
            Assert.False(await secondHost.TryRenew(duration, cancellationToken));
            await secondHost.Release(cancellationToken);
            Assert.True(await firstHost.TryRenew(duration, cancellationToken));
            Assert.False(await secondHost.TryAcquire(duration, cancellationToken));
            await firstHost.Release(cancellationToken);
            Assert.True(await secondHost.TryAcquire(duration, cancellationToken));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenMigrationRunsAgain_ShouldPreserveUniqueIndexAndCompleteReadiness()
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_migration_test_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var cancellationToken = TestContext.Current.CancellationToken;
        var readiness = new MongoMigrationCompletion();
        var runner = new MongoMigrationRunner(database, NullLogger<MongoMigrationRunner>.Instance, readiness);

        try
        {
            await runner.Run(cancellationToken);
            await readiness.Wait(cancellationToken);
            await runner.Run(cancellationToken);
            var records = database.GetCollection<BsonDocument>("NotificationDeliveryRecord");
            using var cursor = await records.Indexes.ListAsync(cancellationToken);
            var indexes = await cursor.ToListAsync(cancellationToken);
            var index = Assert.Single(indexes, index => index["name"] == "notificationKey_unique");
            Assert.True(index["unique"].AsBoolean);
            Assert.Equal(new BsonDocument("notificationKey", 1), index["key"].AsBsonDocument);
            await records.InsertOneAsync(
                new BsonDocument("notificationKey", "duplicate"),
                cancellationToken: cancellationToken
            );
            await Assert.ThrowsAsync<MongoWriteException>(() =>
                records.InsertOneAsync(
                    new BsonDocument("notificationKey", "duplicate"),
                    cancellationToken: cancellationToken
                )
            );

            var migration = new NotificationDeliveryRecordIndexes();
            var context = new MigrationContext(database, null!, cancellationToken);
            await migration.UpAsync(context);
            await migration.DownAsync(context);
            await migration.DownAsync(context);
            await records.DeleteManyAsync(new BsonDocument(), cancellationToken);
            await records.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("wrongField"),
                    new CreateIndexOptions { Name = "notificationKey_unique" }
                ),
                cancellationToken: cancellationToken
            );
            await migration.UpAsync(context);
            using var repairedCursor = await records.Indexes.ListAsync(cancellationToken);
            var repairedIndexes = await repairedCursor.ToListAsync(cancellationToken);
            var repairedIndex = Assert.Single(repairedIndexes, item => item["name"] == "notificationKey_unique");
            Assert.True(repairedIndex["unique"].AsBoolean);
            Assert.Equal(new BsonDocument("notificationKey", 1), repairedIndex["key"].AsBsonDocument);
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenLeaseExpires_ShouldRejectRenewalAndProtectNewOwnerFromFormerOwner()
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_expiry_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var firstHost = new MongoMigrationLeaseService(database, TimeProvider.System);
        var secondHost = new MongoMigrationLeaseService(database, TimeProvider.System);
        var cancellationToken = TestContext.Current.CancellationToken;
        var duration = TimeSpan.FromSeconds(60);

        try
        {
            Assert.True(await firstHost.TryAcquire(duration, cancellationToken));
            await database
                .GetCollection<BsonDocument>("_migrations_lease")
                .UpdateOneAsync(
                    new BsonDocument("_id", "mongo-migrations"),
                    Builders<BsonDocument>.Update.Set("expiresAt", DateTime.UtcNow.AddSeconds(-1)),
                    cancellationToken: cancellationToken
                );

            Assert.False(await firstHost.TryRenew(duration, cancellationToken));
            Assert.True(await secondHost.TryAcquire(duration, cancellationToken));
            Assert.False(await firstHost.TryRenew(duration, cancellationToken));
            await firstHost.Release(cancellationToken);

            Assert.True(await secondHost.TryRenew(duration, cancellationToken));
            Assert.False(await firstHost.TryAcquire(duration, cancellationToken));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }
}
