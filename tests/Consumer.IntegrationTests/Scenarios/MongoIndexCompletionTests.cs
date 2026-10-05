using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Data.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class MongoIndexCompletionTests : IntegrationTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenIndexBuildContinuesAfterClientCancellation_ShouldWaitBeforeSavingHistory(bool hidden)
    {
        var databaseName = $"notifications_build_{Guid.NewGuid():N}";
        var confirmationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        var settings = MongoClientSettings.FromConnectionString("mongodb://localhost:27017");
        settings.ClusterConfigurator = builder =>
            builder.Subscribe<CommandStartedEvent>(started =>
            {
                if (
                    started.CommandName == "createIndexes"
                    && started.DatabaseNamespace.DatabaseName == databaseName
                    && Interlocked.Increment(ref requestCount) == 2
                )
                    confirmationStarted.TrySetResult();
            });
        using var client = new MongoClient(settings);
        var database = client.GetDatabase(databaseName);
        var records = database.GetCollection<BsonDocument>("NotificationDeliveryRecord");
        var token = TestContext.Current.CancellationToken;
        using var cancelledRequest = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? originalBuild = null;
        Task? retry = null;
        try
        {
            await records.InsertOneAsync(new BsonDocument("notificationKey", "seed"), cancellationToken: token);
            await SetBuildFailpoint(client, "alwaysOn", token);
            originalBuild = records.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("notificationKey"),
                    new CreateIndexOptions
                    {
                        Name = "notificationKey_unique",
                        Unique = true,
                        Hidden = hidden,
                    }
                ),
                cancellationToken: cancelledRequest.Token
            );
            await WaitForBuildingIndex(database, token);
            await cancelledRequest.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                originalBuild.WaitAsync(TimeSpan.FromSeconds(5), token)
            );

            var completion = new MongoMigrationCompletion();
            var runner = new MongoMigrationRunner(database, NullLogger<MongoMigrationRunner>.Instance, completion);
            retry = runner.Run(token);
            await confirmationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.False(retry.IsCompleted);
            Assert.False(completion.IsCompleted);
            Assert.False(await runner.CheckCompletion(token));
            Assert.Equal(
                0,
                await database
                    .GetCollection<BsonDocument>("_migrations")
                    .CountDocumentsAsync(new BsonDocument(), cancellationToken: token)
            );

            await SetBuildFailpoint(client, "off", token);
            await retry.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.True(completion.IsCompleted);
            Assert.Equal(
                1,
                await database
                    .GetCollection<BsonDocument>("_migrations")
                    .CountDocumentsAsync(new BsonDocument(), cancellationToken: token)
            );
            using var cursor = await records.Indexes.ListAsync(token);
            var index = Assert.Single(
                await cursor.ToListAsync(token),
                index => index["name"] == "notificationKey_unique"
            );
            Assert.Equal(hidden, index.GetValue("hidden", false).AsBoolean);
            await Assert.ThrowsAsync<MongoWriteException>(() =>
                records.InsertOneAsync(new BsonDocument("notificationKey", "seed"), cancellationToken: token)
            );
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await SetBuildFailpoint(client, "off", cleanup.Token);
            if (originalBuild is not null)
                await originalBuild
                    .ContinueWith(_ => { }, TaskScheduler.Default)
                    .WaitAsync(TimeSpan.FromSeconds(10), cleanup.Token);
            if (retry is not null)
                await retry
                    .ContinueWith(_ => { }, TaskScheduler.Default)
                    .WaitAsync(TimeSpan.FromSeconds(10), cleanup.Token);
            await client.DropDatabaseAsync(databaseName, cleanup.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAppliedIndexIsBeingRebuilt_ShouldKeepNewHostUnreadyUntilBuildCompletes(bool hidden)
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_rebuild_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var records = database.GetCollection<BsonDocument>("NotificationDeliveryRecord");
        var token = TestContext.Current.CancellationToken;
        Task? build = null;
        try
        {
            var firstHost = new MongoMigrationRunner(
                database,
                NullLogger<MongoMigrationRunner>.Instance,
                new MongoMigrationCompletion()
            );
            await firstHost.Run(token);
            await records.InsertOneAsync(new BsonDocument("notificationKey", "seed"), cancellationToken: token);
            await records.Indexes.DropOneAsync("notificationKey_unique", token);
            await SetBuildFailpoint(client, "alwaysOn", token);
            build = records.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("notificationKey"),
                    new CreateIndexOptions
                    {
                        Name = "notificationKey_unique",
                        Unique = true,
                        Hidden = hidden,
                    }
                ),
                cancellationToken: token
            );
            await WaitForBuildingIndex(database, token);
            using var cursor = await records.Indexes.ListAsync(token);
            var listed = Assert.Single(
                await cursor.ToListAsync(token),
                index => index["name"] == "notificationKey_unique"
            );
            Assert.True(listed["unique"].AsBoolean);
            Assert.Equal(new BsonDocument("notificationKey", 1), listed["key"]);
            Assert.DoesNotContain("partialFilterExpression", listed.Names);
            var completion = new MongoMigrationCompletion();
            var peer = new MongoMigrationRunner(database, NullLogger<MongoMigrationRunner>.Instance, completion);
            Assert.False(await peer.CheckCompletion(token));
            Assert.False(completion.IsCompleted);

            await SetBuildFailpoint(client, "off", token);
            await build.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.True(await peer.CheckCompletion(token));
            Assert.True(completion.IsCompleted);
            using var completedCursor = await records.Indexes.ListAsync(token);
            Assert.Equal(
                listed,
                Assert.Single(
                    await completedCursor.ToListAsync(token),
                    index => index["name"] == "notificationKey_unique"
                )
            );
            await Assert.ThrowsAsync<MongoWriteException>(() =>
                records.InsertOneAsync(new BsonDocument("notificationKey", "seed"), cancellationToken: token)
            );
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await SetBuildFailpoint(client, "off", cleanup.Token);
            if (build is not null)
                await build
                    .ContinueWith(_ => { }, TaskScheduler.Default)
                    .WaitAsync(TimeSpan.FromSeconds(10), cleanup.Token);
            await client.DropDatabaseAsync(databaseName, cleanup.Token);
        }
    }

    private static Task<BsonDocument> SetBuildFailpoint(IMongoClient client, string mode, CancellationToken token) =>
        client
            .GetDatabase("admin")
            .RunCommandAsync<BsonDocument>(
                new BsonDocument { { "configureFailPoint", "hangAfterStartingIndexBuildUnlocked" }, { "mode", mode } },
                cancellationToken: token
            );

    private static Task WaitForBuildingIndex(IMongoDatabase database, CancellationToken token) =>
        WaitForAsync(async () =>
        {
            var stats = await database.RunCommandAsync<BsonDocument>(
                new BsonDocument("collStats", "NotificationDeliveryRecord"),
                cancellationToken: token
            );
            Assert.Contains("notificationKey_unique", stats["indexBuilds"].AsBsonArray);
        });
}
