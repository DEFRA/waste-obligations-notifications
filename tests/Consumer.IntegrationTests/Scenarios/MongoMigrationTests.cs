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
        var readiness = new MongoMigrationReadiness();
        var runner = new MongoMigrationRunner(database, NullLogger<MongoMigrationRunner>.Instance, readiness);

        try
        {
            await runner.Run(cancellationToken);
            await readiness.Wait(cancellationToken);
            await runner.Run(cancellationToken);
            var records = database.GetCollection<BsonDocument>("notificationDeliveryRecords");
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
            await migration.UpAsync(context);
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }
}
