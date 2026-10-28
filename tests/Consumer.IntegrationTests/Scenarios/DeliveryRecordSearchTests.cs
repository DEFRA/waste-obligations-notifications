using Defra.WasteObligations.Consumer.Administration;
using Defra.WasteObligations.Consumer.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class DeliveryRecordSearchTests : IntegrationTestBase
{
    [Fact]
    public async Task WhenSearchingBySourceAndRecipient_ShouldReturnOnlyMatchingEvidence()
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_source_search_{Guid.NewGuid():N}";
        var records = client.GetDatabase(databaseName).GetCollection<BsonDocument>("NotificationDeliveryRecord");
        var token = TestContext.Current.CancellationToken;
        var time = DateTime.UtcNow;
        var first = Record(1, "v1:first", "delivery-accepted", time);
        first["source"] = new BsonDocument { { "type", "compliance-declaration" }, { "id", "declaration-1" } };
        first["recipient"] = "recipient-digest";
        first["correlationId"] = "4bf92f3577b34da6a3ce929d0e0e4736";
        var other = Record(2, "v1:other", "delivery-accepted", time);
        other["source"] = new BsonDocument { { "type", "another-source" }, { "id", "declaration-1" } };
        other["recipient"] = "recipient-digest";
        var third = Record(3, "v1:third", "delivery-accepted", time);
        third["source"] = first["source"].DeepClone();
        third["recipient"] = "other-digest";
        try
        {
            await records.InsertManyAsync([first, other, third], cancellationToken: token);
            var result = await Reader(client, databaseName)
                .Search(
                    new DeliveryRecordSearchQuery
                    {
                        SourceType = "compliance-declaration",
                        SourceId = "declaration-1",
                        RecipientDigest = "recipient-digest",
                        CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
                    },
                    token
                );
            var record = Assert.Single(result.Records);
            Assert.Equal("v1:first", record.NotificationKey);
            Assert.Equal("compliance-declaration", record.Source?.Type);
            Assert.Equal("declaration-1", record.Source?.Id);
            Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", record.CorrelationId);
            Assert.Empty(
                (
                    await Reader(client, databaseName)
                        .Search(
                            new DeliveryRecordSearchQuery { CorrelationId = "59a48ed1-c28b-4126-bbc5-46b0461798b8" },
                            token
                        )
                ).Records
            );
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenSearchingRecordingRange_ShouldApplyBoundsAndCombinedExactFiltersWithoutChangingEvidence()
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_search_test_{Guid.NewGuid():N}";
        var records = client.GetDatabase(databaseName).GetCollection<BsonDocument>("NotificationDeliveryRecord");
        var token = TestContext.Current.CancellationToken;
        var from = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(1);
        var fixtures = new[]
        {
            Record(1, "v1:replacement", "delivery-accepted", from, "notify-1"),
            Record(2, "v1:replacement-other", "delivery-accepted", from.AddHours(1), "notify-1"),
            Record(3, "v1:replacement-upper", "delivery-accepted", to, "notify-1"),
            Record(4, "v1:replacement-before", "delivery-accepted", from.AddMilliseconds(-1), "notify-1"),
            Record(5, "v1:replacement-pending", "delivery-pending", from, null),
            Record(6, "v1:replacement-case-notify", "delivery-accepted", from, "NOTIFY-1"),
            Record(7, "v1:replacement-other-parent", "delivery-accepted", from, "notify-1"),
            Record(8, "V1:replacement", "delivery-accepted", from, "notify-1"),
        };
        try
        {
            using var loggerFactory = LoggerFactory.Create(_ => { });
            await new MongoMigrationRunner(
                client.GetDatabase(databaseName),
                loggerFactory.CreateLogger<MongoMigrationRunner>(),
                new MongoMigrationCompletion()
            ).Run(token);
            await records.InsertManyAsync(fixtures, cancellationToken: token);
            var before = await records.Find(new BsonDocument()).Sort("{_id:1}").ToListAsync(token);
            var reader = Reader(client, databaseName);
            var result = await reader.Search(
                new DeliveryRecordSearchQuery
                {
                    RecordedFromUtc = from,
                    RecordedToUtc = to,
                    NotifyNotificationId = "notify-1",
                    NotificationKey = "v1:replacement",
                    Outcome = "delivery-accepted",
                },
                token
            );

            var record = Assert.Single(result.Records);
            Assert.Equal(from, record.RecordedAtUtc);
            Assert.Equal(new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc), record.ActionOccurredAtUtc);
            Assert.Equal("notify-1", record.NotifyNotificationId);
            Assert.Equal("v1:reference", record.NotifyReference);
            Assert.Null(result.NextCursor);
            foreach (
                var exclusion in new[]
                {
                    new DeliveryRecordSearchQuery
                    {
                        NotificationKey = "v1:replacement",
                        NotifyNotificationId = "NOTIFY-1",
                    },
                    new DeliveryRecordSearchQuery { NotificationKey = "v1:replacement", Outcome = "delivery-pending" },
                    new DeliveryRecordSearchQuery
                    {
                        NotificationKey = "v1:replacement",
                        RecordedFromUtc = from.AddHours(1),
                    },
                    new DeliveryRecordSearchQuery { NotificationKey = "v1:replacement", RecordedToUtc = from },
                }
            )
                Assert.Empty((await reader.Search(exclusion, token)).Records);
            var notify = await reader.Search(
                new DeliveryRecordSearchQuery { NotifyNotificationId = "notify-1" },
                token
            );
            Assert.Equal(6, notify.Records.Count);
            Assert.DoesNotContain(notify.Records, item => item.NotificationKey == "v1:replacement-case-notify");
            Assert.DoesNotContain(notify.Records, item => item.NotificationKey == "v1:replacement-pending");
            var range = await reader.Search(
                new DeliveryRecordSearchQuery { RecordedFromUtc = from, RecordedToUtc = to },
                token
            );
            Assert.Equal(6, range.Records.Count);
            Assert.All(range.Records, item => Assert.Equal(from.Date, item.RecordedAtUtc.Date));
            var fractionalLower = await reader.Search(
                new DeliveryRecordSearchQuery { RecordedFromUtc = from.AddTicks(1), RecordedToUtc = to },
                token
            );
            Assert.Equal("v1:replacement-other", Assert.Single(fractionalLower.Records).NotificationKey);
            var fractionalUpper = await reader.Search(
                new DeliveryRecordSearchQuery { RecordedFromUtc = from, RecordedToUtc = from.AddTicks(1) },
                token
            );
            Assert.Equal(5, fractionalUpper.Records.Count);
            Assert.Equal(before, await records.Find(new BsonDocument()).Sort("{_id:1}").ToListAsync(token));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenRecordingTimesTie_ShouldTraverseEveryOutcomeNewestFirstWithoutRepeatingRecords()
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_search_pages_{Guid.NewGuid():N}";
        var records = client.GetDatabase(databaseName).GetCollection<BsonDocument>("NotificationDeliveryRecord");
        var token = TestContext.Current.CancellationToken;
        var time = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var fixtures = new[]
        {
            Record(1, "v1:old", "delivery-suppressed", time.AddMinutes(-1)),
            Record(2, "v1:abandoned", "delivery-abandoned", time),
            Record(3, "v1:pending", "delivery-pending", time),
            Record(4, "v1:accepted", "delivery-accepted", time, "notify-id"),
            Record(5, "v1:new", "future-retained-outcome", time.AddMinutes(1)),
        };
        const string parent = "v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        foreach (var replacement in fixtures.Skip(1).Take(3))
            replacement["resendOfNotificationKey"] = parent;
        try
        {
            using var loggerFactory = LoggerFactory.Create(_ => { });
            await new MongoMigrationRunner(
                client.GetDatabase(databaseName),
                loggerFactory.CreateLogger<MongoMigrationRunner>(),
                new MongoMigrationCompletion()
            ).Run(token);
            await records.InsertManyAsync(fixtures, cancellationToken: token);
            var before = await records.Find(new BsonDocument()).Sort("{_id:1}").ToListAsync(token);
            var reader = Reader(client, databaseName);
            var first = await reader.Search(new DeliveryRecordSearchQuery { PageSize = 2 }, token);
            Assert.Collection(
                first.Records,
                record => Assert.Equal("v1:new", record.NotificationKey),
                record => Assert.Equal("v1:accepted", record.NotificationKey)
            );
            Assert.NotNull(first.NextCursor);
            var second = await reader.Search(
                new DeliveryRecordSearchQuery { PageSize = 2, Cursor = first.NextCursor },
                token
            );
            Assert.Collection(
                second.Records,
                record => Assert.Equal("v1:pending", record.NotificationKey),
                record => Assert.Equal("v1:abandoned", record.NotificationKey)
            );
            Assert.NotNull(second.NextCursor);
            var third = await reader.Search(
                new DeliveryRecordSearchQuery { PageSize = 2, Cursor = second.NextCursor },
                token
            );
            Assert.Equal("v1:old", Assert.Single(third.Records).NotificationKey);
            Assert.Null(third.NextCursor);
            var pending = await reader.Search(new DeliveryRecordSearchQuery { NotificationKey = "v1:pending" }, token);
            var parentQuery = new DeliveryRecordSearchQuery { ResendOfNotificationKey = parent, PageSize = 1 };
            var parentFirst = await reader.Search(parentQuery, token);
            Assert.Equal("v1:accepted", Assert.Single(parentFirst.Records).NotificationKey);
            var parentSecond = await reader.Search(parentQuery with { Cursor = parentFirst.NextCursor }, token);
            Assert.Equal("v1:pending", Assert.Single(parentSecond.Records).NotificationKey);
            var parentThird = await reader.Search(parentQuery with { Cursor = parentSecond.NextCursor }, token);
            Assert.Equal("v1:abandoned", Assert.Single(parentThird.Records).NotificationKey);
            Assert.Null(parentThird.NextCursor);
            Assert.All(
                parentFirst.Records.Concat(parentSecond.Records).Concat(parentThird.Records),
                record => Assert.Equal(parent, record.ResendOfNotificationKey)
            );
            await Assert.ThrowsAsync<ArgumentException>(() =>
                reader.Search(
                    parentQuery with
                    {
                        Cursor = parentFirst.NextCursor,
                        ResendOfNotificationKey = "another-parent",
                    },
                    token
                )
            );
            var acceptedReplacement = await reader.Search(parentQuery with { Outcome = "delivery-accepted" }, token);
            Assert.Equal("v1:accepted", Assert.Single(acceptedReplacement.Records).NotificationKey);
            Assert.Equal("delivery-pending", Assert.Single(pending.Records).Outcome);
            Assert.Null(pending.Records[0].NotifyNotificationId);
            var missingId = await reader.Search(
                new DeliveryRecordSearchQuery { NotifyNotificationId = "absent" },
                token
            );
            Assert.Empty(missingId.Records);
            var future = await reader.Search(
                new DeliveryRecordSearchQuery { Outcome = "future-retained-outcome" },
                token
            );
            Assert.Equal("v1:new", Assert.Single(future.Records).NotificationKey);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                reader.Search(
                    new DeliveryRecordSearchQuery { Cursor = first.NextCursor, Outcome = "delivery-accepted" },
                    token
                )
            );
            var accepted = await reader.Search(new DeliveryRecordSearchQuery { Outcome = "delivery-accepted" }, token);
            Assert.Equal("v1:accepted", Assert.Single(accepted.Records).NotificationKey);
            Assert.Equal(before, await records.Find(new BsonDocument()).Sort("{_id:1}").ToListAsync(token));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenRetainedBsonIsMalformed_ShouldPageReadableEvidenceWithoutInventingFieldsOrChangingRecords()
    {
        using var client = CreateMongoClient();
        var databaseName = $"notifications_bson_search_{Guid.NewGuid():N}";
        var database = client.GetDatabase(databaseName);
        var records = database.GetCollection<BsonDocument>("NotificationDeliveryRecord");
        var token = TestContext.Current.CancellationToken;
        var time = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var valid = new[]
        {
            Record(1, "v1:unknown", "future-retained-outcome", time.AddMinutes(-1)),
            Record(2, "v1:pending", "delivery-pending", time),
            Record(3, "v1:bad-optionals", "delivery-accepted", time),
            Record(4, "v1:wide-version", "delivery-accepted", time),
        };
        valid[2]["templateVersion"] = new BsonInt64(long.MaxValue);
        valid[2]["acceptedAtUtc"] = "not-a-date";
        valid[2]["leaseExpiresAtUtc"] = new BsonDateTime(long.MaxValue);
        valid[2]["resendOfNotificationKey"] = 42;
        valid[2]["notifyNotificationId"] = new BsonArray { "not-a-scalar" };
        valid[2]["notifyReference"] = new BsonDocument("not", "a-string");
        valid[2]["templateId"] = false;
        valid[3]["templateVersion"] = new BsonInt64(7);
        (string Field, BsonValue? Value)[] damage =
        [
            ("actionOccurredAtUtc", null),
            ("recordedAtUtc", new BsonString("not-a-date")),
            ("recordedAtUtc", new BsonArray { time }),
            ("notificationType", new BsonArray { "submitted" }),
            ("outcome", new BsonInt32(42)),
            ("_id", new BsonString("non-object-id")),
            ("recordedAtUtc", new BsonDateTime(long.MaxValue)),
            ("actionOccurredAtUtc", new BsonDateTime(long.MinValue)),
        ];
        var damaged = damage
            .Select(
                (entry, index) =>
                {
                    var record = Record(index + 10, "v1:damaged-" + index, "delivery-accepted", time.AddDays(1));
                    if (entry.Value is null)
                        record.Remove(entry.Field);
                    else
                        record[entry.Field] = entry.Value;

                    return record;
                }
            )
            .ToArray();
        try
        {
            using var loggerFactory = LoggerFactory.Create(_ => { });
            await new MongoMigrationRunner(
                database,
                loggerFactory.CreateLogger<MongoMigrationRunner>(),
                new MongoMigrationCompletion()
            ).Run(token);
            await records.InsertManyAsync(valid.Concat(damaged), cancellationToken: token);
            var before = await records.Find(new BsonDocument()).Sort("{_id:1}").ToListAsync(token);
            var reader = Reader(client, databaseName);
            var first = await reader.Search(new DeliveryRecordSearchQuery { PageSize = 2 }, token);
            Assert.Collection(
                first.Records,
                record =>
                {
                    Assert.Equal("v1:wide-version", record.NotificationKey);
                    Assert.Equal(7, record.TemplateVersion);
                },
                record =>
                {
                    Assert.Equal("v1:bad-optionals", record.NotificationKey);
                    Assert.Null(record.TemplateVersion);
                    Assert.Null(record.AcceptedAtUtc);
                    Assert.Null(record.LeaseExpiresAtUtc);
                    Assert.Null(record.NotifyNotificationId);
                    Assert.Null(record.NotifyReference);
                    Assert.Null(record.TemplateId);
                }
            );
            Assert.NotNull(first.NextCursor);
            var second = await reader.Search(
                new DeliveryRecordSearchQuery { PageSize = 2, Cursor = first.NextCursor },
                token
            );
            Assert.Collection(
                second.Records,
                record => Assert.Equal("v1:pending", record.NotificationKey),
                record => Assert.Equal("v1:unknown", record.NotificationKey)
            );
            Assert.Null(second.NextCursor);
            Assert.Equal("future-retained-outcome", second.Records[1].Outcome);
            var arrayLookup = await reader.Search(
                new DeliveryRecordSearchQuery { NotifyNotificationId = "not-a-scalar" },
                token
            );
            Assert.Empty(arrayLookup.Records);
            foreach (var index in Enumerable.Range(0, damage.Length))
                Assert.Empty(
                    (
                        await reader.Search(
                            new DeliveryRecordSearchQuery { NotificationKey = "v1:damaged-" + index },
                            token
                        )
                    ).Records
                );
            Assert.Equal(before, await records.Find(new BsonDocument()).Sort("{_id:1}").ToListAsync(token));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

    private static MongoDeliveryRecordSearchReader Reader(IMongoClient client, string databaseName) =>
        new(
            client,
            Options.Create(
                new MongoDbOptions { DatabaseUri = "mongodb://localhost:27017", DatabaseName = databaseName }
            ),
            Options.Create(new CommandDlqAdministrationOptions())
        );

    private static BsonDocument Record(
        int id,
        string key,
        string outcome,
        DateTime recordedAtUtc,
        string? notifyId = null
    )
    {
        var record = new BsonDocument
        {
            { "_id", ObjectId.Parse(id.ToString("x24", System.Globalization.CultureInfo.InvariantCulture)) },
            { "notificationKey", key },
            { "immutableFields", "private-immutable" },
            { "recipient", "private-recipient-digest" },
            { "attemptOwner", "private-attempt-owner" },
            { "notificationType", "submitted" },
            { "actionOccurredAtUtc", new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc) },
            { "recordedAtUtc", recordedAtUtc },
            { "outcome", outcome },
        };
        if (notifyId is not null)
        {
            record.Add("notifyNotificationId", notifyId);
            record.Add("notifyReference", "v1:reference");
            record.Add("acceptedAtUtc", recordedAtUtc);
        }

        return record;
    }
}
