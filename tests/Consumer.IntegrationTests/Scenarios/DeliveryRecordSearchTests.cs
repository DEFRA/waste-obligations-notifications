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
            Record(1, "v1:replacement", "delivery-accepted", from, "notify-1", "v1:parent/+?"),
            Record(2, "v1:replacement-other", "delivery-accepted", from.AddHours(1), "notify-1", "v1:parent/+?"),
            Record(3, "v1:replacement-upper", "delivery-accepted", to, "notify-1", "v1:parent/+?"),
            Record(4, "v1:replacement-before", "delivery-accepted", from.AddMilliseconds(-1), "notify-1", "v1:parent/+?"),
            Record(5, "v1:replacement-pending", "delivery-pending", from, null, "v1:parent/+?"),
            Record(6, "v1:replacement-case-notify", "delivery-accepted", from, "NOTIFY-1", "v1:parent/+?"),
            Record(7, "v1:replacement-other-parent", "delivery-accepted", from, "notify-1", "v1:other"),
            Record(8, "V1:replacement", "delivery-accepted", from, "notify-1", "v1:parent/+?"),
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
                    ResendOfNotificationKey = "v1:parent/+?",
                    Outcome = "delivery-accepted",
                },
                token
            );

            var record = Assert.Single(result.Records);
            Assert.Equal(from, record.RecordedAtUtc);
            Assert.Equal(new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc), record.ActionOccurredAtUtc);
            Assert.Equal("notify-1", record.NotifyNotificationId);
            Assert.Equal("v1:reference", record.NotifyReference);
            Assert.Equal(
                "/admin/notification-commands/delivery-records?notificationKey=v1%3Aparent%2F%2B%3F",
                record.Links?.SourceRecord
            );
            Assert.Null(result.NextCursor);
            foreach (var exclusion in new[]
            {
                new DeliveryRecordSearchQuery { NotificationKey = "v1:replacement", NotifyNotificationId = "NOTIFY-1" },
                new DeliveryRecordSearchQuery { NotificationKey = "v1:replacement", ResendOfNotificationKey = "v1:other" },
                new DeliveryRecordSearchQuery { NotificationKey = "v1:replacement", Outcome = "delivery-pending" },
                new DeliveryRecordSearchQuery { NotificationKey = "v1:replacement", RecordedFromUtc = from.AddHours(1) },
                new DeliveryRecordSearchQuery { NotificationKey = "v1:replacement", RecordedToUtc = from },
            })
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
            var parents = await reader.Search(
                new DeliveryRecordSearchQuery { ResendOfNotificationKey = "v1:parent/+?" },
                token
            );
            Assert.Equal(7, parents.Records.Count);
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
            var unknownParent = await reader.Search(
                new DeliveryRecordSearchQuery { ResendOfNotificationKey = "v1:absent" },
                token
            );
            Assert.Empty(unknownParent.Records);
            Assert.Equal(before, await records.Find(new BsonDocument()).Sort("{_id:1}").ToListAsync(token));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenRecordingTimesTie_ShouldTraverseEveryOutcomeNewestFirstWithoutRepeatingOrInventingParents()
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
            Assert.Equal(new[] { "v1:new", "v1:accepted" }, first.Records.Select(record => record.NotificationKey));
            Assert.NotNull(first.NextCursor);
            var second = await reader.Search(
                new DeliveryRecordSearchQuery { PageSize = 2, Cursor = first.NextCursor },
                token
            );
            Assert.Equal(new[] { "v1:pending", "v1:abandoned" }, second.Records.Select(record => record.NotificationKey));
            Assert.NotNull(second.NextCursor);
            var third = await reader.Search(
                new DeliveryRecordSearchQuery { PageSize = 2, Cursor = second.NextCursor },
                token
            );
            Assert.Equal("v1:old", Assert.Single(third.Records).NotificationKey);
            Assert.Null(third.NextCursor);
            Assert.All(first.Records.Concat(second.Records).Concat(third.Records), record =>
            {
                Assert.Null(record.ResendOfNotificationKey);
                Assert.Null(record.Links);
            });
            var pending = await reader.Search(
                new DeliveryRecordSearchQuery { NotificationKey = "v1:pending" },
                token
            );
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
            var accepted = await reader.Search(
                new DeliveryRecordSearchQuery { Outcome = "delivery-accepted" },
                token
            );
            Assert.Equal("v1:accepted", Assert.Single(accepted.Records).NotificationKey);
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
        string? notifyId = null,
        string? parent = null
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
        if (parent is not null)
            record.Add("resendOfNotificationKey", parent);

        return record;
    }
}
