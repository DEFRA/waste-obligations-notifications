using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Data.Migrations;
using Defra.WasteObligations.Consumer.IntegrationTests.Fixtures;
using Defra.WasteObligations.Consumer.Utils.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class MongoMigrationTests : IntegrationTestBase
{
    [Fact]
    public async Task WhenOptionalMigrationFails_ShouldRetainCriticalReadinessOnThisAndAnotherHost()
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_optional_test_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var token = TestContext.Current.CancellationToken;
        var completion = new MongoMigrationCompletion();
        var runner = new MongoMigrationRunner(
            database,
            NullLogger<MongoMigrationRunner>.Instance,
            completion,
            typeof(CriticalStartupFixtureMigration).Assembly
        );
        Task? attempt = null;
        var control = database.GetCollection<BsonDocument>("startup_fixture");
        try
        {
            Assert.False(await runner.CheckCompletion(token));
            Assert.False(completion.IsCompleted);
            await control.InsertOneAsync(new BsonDocument("_id", "hold-optional"), cancellationToken: token);
            attempt = runner.Run(token);
            await WaitForAsync(() =>
            {
                Assert.True(completion.IsCompleted);
                Assert.False(attempt.IsCompleted);

                return Task.CompletedTask;
            });
            Assert.False(await runner.CheckCompletion(token));
            var healthCheck = new MongoMigrationCompletionHealthCheck(completion);
            var health = await healthCheck.CheckHealthAsync(new HealthCheckContext(), token);
            Assert.Equal(HealthStatus.Healthy, health.Status);
            var peerCompletion = new MongoMigrationCompletion();
            var peer = new MongoMigrationRunner(
                database,
                NullLogger<MongoMigrationRunner>.Instance,
                peerCompletion,
                typeof(CriticalStartupFixtureMigration).Assembly
            );
            Assert.False(await peer.CheckCompletion(token));
            Assert.True(peerCompletion.IsCompleted);
            await control.DeleteOneAsync(new BsonDocument("_id", "hold-optional"), token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => attempt);
            Assert.Equal(
                HealthStatus.Healthy,
                (await healthCheck.CheckHealthAsync(new HealthCheckContext(), token)).Status
            );
            var history = await database
                .GetCollection<BsonDocument>("_migrations")
                .Find(FilterDefinition<BsonDocument>.Empty)
                .ToListAsync(token);
            var appliedMigration = Assert.Single(history);
            Assert.Equal("1.0.0", appliedMigration["v"].AsString);
            Assert.False(new OptionalStartupFixtureMigration().Critical);
        }
        finally
        {
            await control.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, CancellationToken.None);
            if (attempt is not null)
            {
                try
                {
                    await attempt;
                }
                catch (InvalidOperationException)
                {
                    // The fixture deliberately rejects optional work.
                }
            }
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

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
        var migratingRunner = new MongoMigrationRunner(
            database,
            NullLogger<MongoMigrationRunner>.Instance,
            new MongoMigrationCompletion()
        );

        try
        {
            Assert.False(await waitingRunner.CheckCompletion(cancellationToken));
            var migration = new NotificationDeliveryRecordIndexes();
            await migration.UpAsync(new MigrationContext(database, null!, cancellationToken));
            Assert.False(await waitingRunner.CheckCompletion(cancellationToken));
            Assert.False(waitingReadiness.IsCompleted);

            await migratingRunner.Run(cancellationToken);

            Assert.True(await waitingRunner.CheckCompletion(cancellationToken));
            Assert.True(waitingReadiness.IsCompleted);
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
    [InlineData("partial")]
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
                        new CreateIndexOptions<BsonDocument>
                        {
                            Name = "notificationKey_unique",
                            Unique = invalidIndex != "non-unique",
                            PartialFilterExpression =
                                invalidIndex == "partial" ? new BsonDocument("outcome", "delivery-accepted") : null,
                        }
                    ),
                    cancellationToken: cancellationToken
                );
            }
            var readiness = new MongoMigrationCompletion();
            var runner = new MongoMigrationRunner(database, NullLogger<MongoMigrationRunner>.Instance, readiness);

            Assert.False(await runner.CheckCompletion(cancellationToken));
            Assert.False(readiness.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.Run(cancellationToken));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("wrong-key")]
    [InlineData("non-unique")]
    public async Task WhenRequiredIndexIsIncompatible_ShouldRejectMigrationAndPreserveExistingIndex(string invalidIndex)
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_partial_index_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var token = TestContext.Current.CancellationToken;
        var completion = new MongoMigrationCompletion();
        var runner = new MongoMigrationRunner(database, NullLogger<MongoMigrationRunner>.Instance, completion);
        var records = database.GetCollection<BsonDocument>("NotificationDeliveryRecord");

        try
        {
            await records.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending(
                        invalidIndex == "wrong-key" ? "wrongField" : "notificationKey"
                    ),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Name = "notificationKey_unique",
                        Unique = invalidIndex != "non-unique",
                        PartialFilterExpression =
                            invalidIndex == "partial" ? new BsonDocument("outcome", "delivery-accepted") : null,
                    }
                ),
                cancellationToken: token
            );

            using var beforeCursor = await records.Indexes.ListAsync(token);
            var before = Assert.Single(
                await beforeCursor.ToListAsync(token),
                index => index["name"] == "notificationKey_unique"
            );

            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.Run(token));

            Assert.False(completion.IsCompleted);
            Assert.False(await runner.CheckCompletion(token));
            using var cursor = await records.Indexes.ListAsync(token);
            var index = Assert.Single(
                await cursor.ToListAsync(token),
                index => index["name"] == "notificationKey_unique"
            );
            Assert.Equal(before, index);
            Assert.Equal(
                0,
                await database
                    .GetCollection<BsonDocument>("_migrations")
                    .CountDocumentsAsync(new BsonDocument(), cancellationToken: token)
            );
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
        var logger = new RecordingMigrationLogger();
        var runner = new MongoMigrationRunner(database, logger, readiness);

        try
        {
            await runner.Run(cancellationToken);
            Assert.True(readiness.IsCompleted);
            Assert.Equal([1], logger.AppliedMigrationCounts);
            await runner.Run(cancellationToken);
            Assert.Equal([1, 0], logger.AppliedMigrationCounts);
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
            await Assert.ThrowsAsync<InvalidOperationException>(() => migration.UpAsync(context));
            using var preservedCursor = await records.Indexes.ListAsync(cancellationToken);
            var preservedIndexes = await preservedCursor.ToListAsync(cancellationToken);
            var preservedIndex = Assert.Single(preservedIndexes, item => item["name"] == "notificationKey_unique");
            Assert.False(preservedIndex.GetValue("unique", false).AsBoolean);
            Assert.Equal(new BsonDocument("wrongField", 1), preservedIndex["key"].AsBsonDocument);
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

    private sealed class RecordingMigrationLogger : ILogger<MongoMigrationRunner>
    {
        public List<int> AppliedMigrationCounts { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel != LogLevel.Information || state is not IEnumerable<KeyValuePair<string, object?>> properties)
                return;

            foreach (var property in properties)
            {
                if (property.Key == "AppliedMigrationCount" && property.Value is int count)
                    AppliedMigrationCounts.Add(count);
            }
        }
    }
}
