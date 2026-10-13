using System.Collections.Concurrent;
using System.Globalization;
using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using Defra.WasteObligations.Consumer.Administration;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Data.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class DeliveryRecordSearchQueryPlanTests : IntegrationTestBase
{
    private const string CollectionName = "NotificationDeliveryRecord";
    private const string NotifyId = "retained-notify-id";
    private const string ParentKey = "v1:retained-parent";
    private const string AcceptedOutcome = "delivery-accepted";

    [Fact]
    public async Task WhenSearchIndexesAreAdded_ShouldReplaceActualListingScanWithBoundedOrderedReads()
    {
        var databaseName = $"notifications_search_plans_{Guid.NewGuid():N}";
        var commands = new ConcurrentQueue<(string Database, BsonDocument Command)>();
        var settings = MongoClientSettings.FromConnectionString("mongodb://localhost:27017");
        settings.ApplicationName = "delivery-record-search-query-plan-test";
        settings.ClusterConfigurator = cluster =>
            cluster.Subscribe<CommandStartedEvent>(started =>
            {
                if (started.CommandName == "find" && started.Command.GetValue("find", "") == CollectionName)
                {
                    // Materialize the monitored raw BSON while its callback buffer is valid.
                    var command = BsonSerializer.Deserialize<BsonDocument>(started.Command.ToBson());
                    commands.Enqueue((started.DatabaseNamespace.DatabaseName, command));
                }
            });
        using var client = new MongoClient(settings);
        var database = client.GetDatabase(databaseName);
        var records = database.GetCollection<BsonDocument>(CollectionName);
        var token = TestContext.Current.CancellationToken;
        var time = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var reader = new MongoDeliveryRecordSearchReader(
            client,
            Options.Create(
                new MongoDbOptions { DatabaseUri = "mongodb://localhost:27017", DatabaseName = databaseName }
            ),
            Options.Create(new CommandDlqAdministrationOptions())
        );
        try
        {
            await new NotificationDeliveryRecordIndexes().UpAsync(new MigrationContext(database, null!, token));
            var fixtures = Enumerable
                .Range(1, 1000)
                .Select(index =>
                    Record(
                        index + 1000,
                        "v1:distractor-" + index.ToString(CultureInfo.InvariantCulture),
                        time.AddDays(-2).AddMilliseconds(index),
                        "delivery-suppressed"
                    )
                )
                .Concat(
                    Enumerable
                        .Range(1, 40)
                        .Select(index =>
                            Record(
                                index,
                                "v1:target-" + index.ToString("D2", CultureInfo.InvariantCulture),
                                index <= 20 ? time.AddMinutes(-1) : time,
                                AcceptedOutcome,
                                NotifyId,
                                ParentKey
                            )
                        )
                )
                .Append(Record(41, "v1:new-pending", time.AddMinutes(1), "delivery-pending"));
            await records.InsertManyAsync(fixtures, cancellationToken: token);
            Assert.Equal(1041, await records.CountDocumentsAsync(new BsonDocument(), cancellationToken: token));
            var defaultQuery = new DeliveryRecordSearchQuery { PageSize = 5 };
            var before = await SearchAndExplain(reader, database, commands, defaultQuery, token);
            string[] firstKeys = ["v1:new-pending", "v1:target-40", "v1:target-39", "v1:target-38", "v1:target-37"];
            Assert.Equal(firstKeys, before.Result.Records.Select(record => record.NotificationKey));
            var beforeStages = WinningStages(before.Explain).ToArray();
            Assert.Contains("COLLSCAN", beforeStages);
            Assert.Contains("SORT", beforeStages);
            Assert.Equal(1041, before.Explain["executionStats"]["totalDocsExamined"].ToInt64());
            var completion = new MongoMigrationCompletion();
            await new MongoMigrationRunner(database, NullLogger<MongoMigrationRunner>.Instance, completion).Run(token);
            Assert.True(completion.IsCompleted);
            var after = await SearchAndExplain(reader, database, commands, defaultQuery, token);
            Assert.Equal(before.Result.Records, after.Result.Records);
            Assert.Equal(before.Result.NextCursor, after.Result.NextCursor);
            AssertOrderedBounded(after.Explain);
            Assert.NotNull(after.Result.NextCursor);
            var continuation = await SearchAndExplain(
                reader,
                database,
                commands,
                defaultQuery with
                {
                    Cursor = after.Result.NextCursor,
                },
                token
            );
            string[] continuationKeys =
            [
                "v1:target-36",
                "v1:target-35",
                "v1:target-34",
                "v1:target-33",
                "v1:target-32",
            ];
            Assert.Equal(continuationKeys, continuation.Result.Records.Select(record => record.NotificationKey));
            Assert.NotNull(continuation.Result.NextCursor);
            AssertOrderedBounded(continuation.Explain);
            var filtered = new[]
            {
                new DeliveryRecordSearchQuery
                {
                    PageSize = 5,
                    RecordedFromUtc = time,
                    RecordedToUtc = time.AddTicks(1),
                },
                new DeliveryRecordSearchQuery { PageSize = 5, NotifyNotificationId = NotifyId },
                new DeliveryRecordSearchQuery { PageSize = 5, ResendOfNotificationKey = ParentKey },
                new DeliveryRecordSearchQuery { PageSize = 5, Outcome = AcceptedOutcome },
                new DeliveryRecordSearchQuery
                {
                    PageSize = 5,
                    RecordedFromUtc = time,
                    RecordedToUtc = time.AddTicks(1),
                    NotifyNotificationId = NotifyId,
                    ResendOfNotificationKey = ParentKey,
                    Outcome = AcceptedOutcome,
                },
            };
            string[] filteredKeys = ["v1:target-40", "v1:target-39", "v1:target-38", "v1:target-37", "v1:target-36"];
            foreach (var query in filtered)
            {
                var actual = await SearchAndExplain(reader, database, commands, query, token);
                Assert.Equal(filteredKeys, actual.Result.Records.Select(record => record.NotificationKey));
                Assert.NotNull(actual.Result.NextCursor);
                AssertOrderedBounded(actual.Explain);
            }
            var exact = await SearchAndExplain(
                reader,
                database,
                commands,
                new DeliveryRecordSearchQuery { NotificationKey = "v1:target-20" },
                token
            );
            Assert.Equal("v1:target-20", Assert.Single(exact.Result.Records).NotificationKey);
            Assert.Null(exact.Result.NextCursor);
            Assert.DoesNotContain("COLLSCAN", WinningStages(exact.Explain));
            Assert.Equal(1, exact.Explain["executionStats"]["nReturned"].ToInt64());
            Assert.InRange(exact.Explain["executionStats"]["totalKeysExamined"].ToInt64(), 1, 1);
            Assert.InRange(exact.Explain["executionStats"]["totalDocsExamined"].ToInt64(), 1, 1);
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

    private static async Task<(DeliveryRecordSearchResult Result, BsonDocument Explain)> SearchAndExplain(
        MongoDeliveryRecordSearchReader reader,
        IMongoDatabase database,
        ConcurrentQueue<(string Database, BsonDocument Command)> commands,
        DeliveryRecordSearchQuery query,
        CancellationToken token
    )
    {
        commands.Clear();
        var result = await reader.Search(query, token);
        var captured = Assert.Single(commands);
        Assert.Equal(database.DatabaseNamespace.DatabaseName, captured.Database);
        Assert.Equal(CollectionName, captured.Command["find"].AsString);
        Assert.True(captured.Command.Contains("filter"));
        Assert.True(captured.Command.Contains("projection"));
        Assert.Equal(new BsonDocument { { "recordedAtUtc", -1 }, { "_id", -1 } }, captured.Command["sort"]);
        Assert.False(captured.Command.Contains("hint"));
        var command = captured.Command.DeepClone().AsBsonDocument;
        // These are driver transport/session metadata, not find filters, projection, sort, bounds or limits.
        foreach (var metadata in new[] { "$db", "lsid", "$clusterTime", "$readPreference" })
            command.Remove(metadata);
        var explain = await database.RunCommandAsync<BsonDocument>(
            new BsonDocument { { "explain", command }, { "verbosity", "executionStats" } },
            cancellationToken: token
        );
        Assert.True(explain["executionStats"]["executionSuccess"].AsBoolean);

        return (result, explain);
    }

    private static void AssertOrderedBounded(BsonDocument explain)
    {
        var stages = WinningStages(explain).ToArray();
        Assert.Contains("IXSCAN", stages);
        Assert.DoesNotContain("COLLSCAN", stages);
        Assert.DoesNotContain("SORT", stages);
        Assert.Equal(6, explain["executionStats"]["nReturned"].ToInt64());
        Assert.InRange(explain["executionStats"]["totalKeysExamined"].ToInt64(), 6, 24);
        Assert.InRange(explain["executionStats"]["totalDocsExamined"].ToInt64(), 6, 12);
    }

    private static IEnumerable<string> WinningStages(BsonDocument explain) =>
        Stages(explain["queryPlanner"]["winningPlan"]);

    private static IEnumerable<string> Stages(BsonValue node)
    {
        if (node is BsonDocument document)
        {
            if (document.TryGetValue("stage", out var stage) && stage.IsString)
                yield return stage.AsString;
            foreach (var value in document.Values)
            {
                foreach (var nested in Stages(value))
                {
                    yield return nested;
                }
            }
        }
        else if (node is BsonArray array)
        {
            foreach (var value in array)
            {
                foreach (var nested in Stages(value))
                {
                    yield return nested;
                }
            }
        }
    }

    private static BsonDocument Record(
        int id,
        string key,
        DateTime recorded,
        string outcome,
        string? notifyId = null,
        string? parent = null
    )
    {
        var record = new BsonDocument
        {
            { "_id", ObjectId.Parse(id.ToString("x24", CultureInfo.InvariantCulture)) },
            { "notificationKey", key },
            { "immutableFields", "v1:synthetic-immutable" },
            { "recipient", "v1:synthetic-recipient" },
            { "notificationType", "submitted" },
            { "actionOccurredAtUtc", DateTime.UnixEpoch },
            { "recordedAtUtc", recorded },
            { "outcome", outcome },
        };
        if (notifyId is not null)
            record.Add("notifyNotificationId", notifyId);
        if (parent is not null)
            record.Add("resendOfNotificationKey", parent);

        return record;
    }
}
