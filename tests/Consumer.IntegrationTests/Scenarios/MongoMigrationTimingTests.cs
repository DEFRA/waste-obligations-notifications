using System.Diagnostics;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.MigrationFixtures;
using Defra.WasteObligations.Consumer.Utils.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class MongoMigrationTimingTests : IntegrationTestBase
{
    [Fact]
    public async Task WhenCriticalStandardCriticalChainRuns_ShouldGiveEachOperationItsBudgetAndWaitForFinalCritical()
    {
        using var client = CreateMongoClient();
        var databaseName = $"migration_timing_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var token = TestContext.Current.CancellationToken;
        var completion = new MongoMigrationCompletion();
        var settings = new MongoMigrationOptions { CriticalOperationTimeoutSeconds = 1, AttemptTimeoutSeconds = 5 };
        var runner = CreateRunner(database, completion, settings);
        var control = database.GetCollection<BsonDocument>("timing_fixture");
        Task? attempt = null;
        try
        {
            await control.InsertManyAsync(
                [new("_id", "hold-standard-2"), new("_id", "hold-critical-3")],
                cancellationToken: token
            );
            attempt = runner.Run(token);
            await WaitForAsync(async () =>
                Assert.True(await control.Find(new BsonDocument("_id", "standard-2-started")).AnyAsync(token))
            );
            await Task.Delay(TimeSpan.FromMilliseconds(1200), token);
            Assert.False(attempt.IsCompleted);
            Assert.False(await runner.CheckCompletion(token));
            Assert.False(completion.IsCompleted);
            var healthCheck = new MongoMigrationCompletionHealthCheck(completion);
            Assert.Equal(
                HealthStatus.Unhealthy,
                (await healthCheck.CheckHealthAsync(new HealthCheckContext(), token)).Status
            );
            await control.DeleteOneAsync(new BsonDocument("_id", "hold-standard-2"), token);

            await attempt.ContinueWith(_ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), token);
            await Assert.ThrowsAsync<TimeoutException>(() => attempt);

            var history = await database
                .GetCollection<BsonDocument>("_migrations")
                .Find(new BsonDocument())
                .ToListAsync(token);
            Assert.Equal(["1.0.0", "2.0.0"], history.Select(entry => entry["v"].AsString).Order());
            Assert.False(completion.IsCompleted);
            await control.DeleteOneAsync(new BsonDocument("_id", "hold-critical-3"), token);
            var peer = CreateRunner(database, new MongoMigrationCompletion(), settings);
            await peer.Run(token);
            Assert.True(await runner.CheckCompletion(token));
            Assert.True(completion.IsCompleted);
            Assert.Equal(
                HealthStatus.Healthy,
                (await healthCheck.CheckHealthAsync(new HealthCheckContext(), token)).Status
            );
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await control.DeleteManyAsync(new BsonDocument(), cleanup.Token);
            if (attempt is not null)
                await attempt
                    .ContinueWith(_ => { }, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10), cleanup.Token);
            await client.DropDatabaseAsync(databaseName, cleanup.Token);
        }
    }

    [Fact]
    public async Task WhenStandardOperationExceedsItsBudget_ShouldCancelWithoutCompletingLaterCriticalPrerequisite()
    {
        using var client = CreateMongoClient();
        var databaseName = $"migration_standard_timeout_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var token = TestContext.Current.CancellationToken;
        var completion = new MongoMigrationCompletion();
        var runner = CreateRunner(database, completion, new MongoMigrationOptions { AttemptTimeoutSeconds = 1 });
        var control = database.GetCollection<BsonDocument>("timing_fixture");
        Task? attempt = null;
        try
        {
            await database
                .GetCollection<BsonDocument>("timing_fixture")
                .InsertOneAsync(new BsonDocument("_id", "hold-standard-2"), cancellationToken: token);
            var started = Stopwatch.GetTimestamp();

            attempt = runner.Run(token);
            await attempt.ContinueWith(_ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), token);
            await Assert.ThrowsAsync<TimeoutException>(() => attempt);

            Assert.InRange(Stopwatch.GetElapsedTime(started).TotalSeconds, 0.9, 5);
            Assert.False(completion.IsCompleted);
            var history = await database
                .GetCollection<BsonDocument>("_migrations")
                .Find(new BsonDocument())
                .ToListAsync(token);
            Assert.Equal("1.0.0", Assert.Single(history)["v"].AsString);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await control.DeleteManyAsync(new BsonDocument(), cleanup.Token);
            if (attempt is not null)
                await attempt
                    .ContinueWith(_ => { }, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10), cleanup.Token);
            await client.DropDatabaseAsync(databaseName, cleanup.Token);
        }
    }

    [Fact]
    public async Task WhenCriticalTimeoutCannotStopWork_ShouldRetainAndRenewLeaseUntilEngineStops()
    {
        using var client = CreateMongoClient();
        var databaseName = $"migration_stalled_timeout_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var token = TestContext.Current.CancellationToken;
        var completion = new MongoMigrationCompletion();
        var settings = new MongoMigrationOptions
        {
            CriticalOperationTimeoutSeconds = 1,
            LeaseDurationSeconds = 10,
            LeaseRenewalIntervalSeconds = 1,
            MaximumAttempts = 1,
        };
        var lease = new MongoMigrationLeaseService(database, TimeProvider.System);
        var peer = new MongoMigrationLeaseService(database, TimeProvider.System);
        using var service = new MongoMigrationService(
            lease,
            CreateRunner(database, completion, settings),
            Options.Create(settings),
            TimeProvider.System,
            NullLogger<MongoMigrationService>.Instance
        );
        var control = database.GetCollection<BsonDocument>("timing_fixture");
        try
        {
            await control.InsertManyAsync(
                [new("_id", "hold-critical-1"), new("_id", "ignore-cancellation")],
                cancellationToken: token
            );
            await service.StartAsync(token);
            await WaitForAsync(async () =>
                Assert.True(await control.Find(new BsonDocument("_id", "critical-1-cancelled")).AnyAsync(token))
            );
            var leases = database.GetCollection<BsonDocument>("_migrations_lease");
            var firstExpiry = (await leases.Find(new BsonDocument()).SingleAsync(token))["expiresAt"].ToUniversalTime();
            await Task.Delay(TimeSpan.FromMilliseconds(1200), token);
            Assert.True(
                (await leases.Find(new BsonDocument()).SingleAsync(token))["expiresAt"].ToUniversalTime() > firstExpiry
            );
            Assert.False(await peer.TryAcquire(TimeSpan.FromSeconds(10), token));
            Assert.False(completion.IsCompleted);
            await control.DeleteOneAsync(new BsonDocument("_id", "hold-critical-1"), token);

            await WaitForAsync(async () => Assert.True(await peer.TryAcquire(TimeSpan.FromSeconds(10), token)));

            Assert.False(completion.IsCompleted);
            Assert.Empty(
                await database.GetCollection<BsonDocument>("_migrations").Find(new BsonDocument()).ToListAsync(token)
            );
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await control.DeleteManyAsync(new BsonDocument(), cleanup.Token);
            await service.StopAsync(cleanup.Token);
            await peer.Release(cleanup.Token);
            await client.DropDatabaseAsync(databaseName, cleanup.Token);
        }
    }

    private static MongoMigrationRunner CreateRunner(
        IMongoDatabase database,
        MongoMigrationCompletion completion,
        MongoMigrationOptions settings
    ) =>
        new(
            database,
            NullLogger<MongoMigrationRunner>.Instance,
            completion,
            typeof(FirstCriticalMigration).Assembly,
            Options.Create(settings),
            TimeProvider.System
        );
}
