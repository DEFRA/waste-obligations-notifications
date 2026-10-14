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
            var newerNonmatches = Enumerable
                .Range(1, 1000)
                .Select(index =>
                    Record(
                        index + 3000,
                        "v1:newer-nonmatch-" + index.ToString(CultureInfo.InvariantCulture),
                        time.AddMinutes(2).AddMilliseconds(index),
                        "delivery-suppressed",
                        index % 2 == 0 ? "nonmatching-notify-id" : null,
                        index % 3 == 0 ? "v1:nonmatching-parent" : null
                    )
                );
            await records.InsertManyAsync(newerNonmatches, cancellationToken: token);
            Assert.Equal(2041, await records.CountDocumentsAsync(new BsonDocument(), cancellationToken: token));
            var notifyQuery = new DeliveryRecordSearchQuery { PageSize = 5, NotifyNotificationId = NotifyId };
            var parentQuery = new DeliveryRecordSearchQuery { PageSize = 5, ResendOfNotificationKey = ParentKey };
            var outcomeQuery = new DeliveryRecordSearchQuery { PageSize = 5, Outcome = AcceptedOutcome };
            var filtered = new[]
            {
                new DeliveryRecordSearchQuery
                {
                    PageSize = 5,
                    RecordedFromUtc = time,
                    RecordedToUtc = time.AddTicks(1),
                },
                notifyQuery,
                parentQuery,
                outcomeQuery,
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
                if (query == notifyQuery)
                {
                    var notifyContinuation = await SearchAndExplain(
                        reader,
                        database,
                        commands,
                        notifyQuery with
                        {
                            Cursor = actual.Result.NextCursor,
                        },
                        token
                    );
                    string[] notifyContinuationKeys =
                    [
                        "v1:target-35",
                        "v1:target-34",
                        "v1:target-33",
                        "v1:target-32",
                        "v1:target-31",
                    ];
                    Assert.Equal(
                        notifyContinuationKeys,
                        notifyContinuation.Result.Records.Select(record => record.NotificationKey)
                    );
                    Assert.NotNull(notifyContinuation.Result.NextCursor);
                    AssertOrderedBounded(notifyContinuation.Explain);
                }
            }
            var controls = new[]
            {
                (Index: "notifyNotificationId_recordedAtUtc_id_desc", Query: notifyQuery),
                (Index: "resendOfNotificationKey_recordedAtUtc_id_desc", Query: parentQuery),
                (Index: "outcome_recordedAtUtc_id_desc", Query: outcomeQuery),
            };
            foreach (var (index, query) in controls)
            {
                await records.Indexes.DropOneAsync(index, token);
                var degraded = await SearchAndExplain(reader, database, commands, query, token);
                Assert.Equal(filteredKeys, degraded.Result.Records.Select(record => record.NotificationKey));
                Assert.NotNull(degraded.Result.NextCursor);
                var stages = WinningStages(degraded.Explain).ToArray();
                Assert.True(
                    stages.Contains("COLLSCAN")
                        || stages.Contains("SORT")
                        || degraded.Explain["executionStats"]["totalKeysExamined"].ToInt64() > 24
                        || degraded.Explain["executionStats"]["totalDocsExamined"].ToInt64() > 12,
                    "Removing the filter index must expose a scan, blocking sort or examined-work regression."
                );
                await new NotificationDeliveryRecordSearchIndexes().UpAsync(
                    new MigrationContext(database, null!, token)
                );
                var restored = await SearchAndExplain(reader, database, commands, query, token);
                Assert.Equal(filteredKeys, restored.Result.Records.Select(record => record.NotificationKey));
                Assert.Equal(degraded.Result.NextCursor, restored.Result.NextCursor);
                Assert.NotNull(restored.Result.NextCursor);
                AssertOrderedBounded(restored.Explain);
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
            var deepRecords = Enumerable
                .Range(1, 600)
                .Select(index =>
                    Record(
                        index + 5000,
                        "v1:deep-" + index.ToString("D3", CultureInfo.InvariantCulture),
                        time.AddMinutes(index <= 300 ? 4 : 5),
                        AcceptedOutcome,
                        NotifyId,
                        ParentKey
                    )
                );
            await records.InsertManyAsync(deepRecords, cancellationToken: token);
            Assert.Equal(2641, await records.CountDocumentsAsync(new BsonDocument(), cancellationToken: token));
            var deepQueries = new[]
            {
                new DeliveryRecordSearchQuery { PageSize = 100 },
                new DeliveryRecordSearchQuery { PageSize = 100, NotifyNotificationId = NotifyId },
            };
            foreach (var initial in deepQueries)
            {
                var paged = initial;
                for (var page = 0; page < 4; page++)
                {
                    var actual = await SearchAndExplain(reader, database, commands, paged, token);
                    var expectedKeys = Enumerable
                        .Range(501 - page * 100, 100)
                        .Reverse()
                        .Select(index => "v1:deep-" + index.ToString("D3", CultureInfo.InvariantCulture));
                    Assert.Equal(expectedKeys, actual.Result.Records.Select(record => record.NotificationKey));
                    Assert.NotNull(actual.Result.NextCursor);
                    paged = initial with { Cursor = actual.Result.NextCursor };
                }
                var deep = await SearchAndExplain(reader, database, commands, paged with { PageSize = 5 }, token);
                string[] deepKeys = ["v1:deep-200", "v1:deep-199", "v1:deep-198", "v1:deep-197", "v1:deep-196"];
                Assert.Equal(deepKeys, deep.Result.Records.Select(record => record.NotificationKey));
                Assert.NotNull(deep.Result.NextCursor);
                AssertOrderedBounded(deep.Explain);
            }
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
