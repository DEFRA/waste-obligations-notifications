using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using Defra.WasteObligations.Consumer.Delivery;
using MongoDB.Bson;
using MigrationVersion = AdaskoTheBeAsT.MongoDbMigrations.Abstractions.Version;

namespace Defra.WasteObligations.Consumer.Data.Migrations;

[MigrationCollection(MongoNotificationDeliveryRecordStore.CollectionName, MigrationDirection.Both)]
public sealed class NotificationDeliveryRecordSearchIndexes : NotificationDeliveryRecordSearchMigration
{
    private const string RecordedAtField = "recordedAtUtc";
    private static readonly (string Name, BsonDocument Keys)[] s_indexes =
    [
        ("recordedAtUtc_id_desc", new BsonDocument { { RecordedAtField, -1 }, { "_id", -1 } }),
        (
            "notifyNotificationId_recordedAtUtc_id_desc",
            new BsonDocument
            {
                { "notifyNotificationId", 1 },
                { RecordedAtField, -1 },
                { "_id", -1 },
            }
        ),
        (
            "outcome_recordedAtUtc_id_desc",
            new BsonDocument
            {
                { "outcome", 1 },
                { RecordedAtField, -1 },
                { "_id", -1 },
            }
        ),
    ];

    public override MigrationVersion Version => new(2, 0, 0);

    public override string Name => "002 - NotificationDeliveryRecord search indexes";

    protected override IReadOnlyList<(string Name, BsonDocument Keys)> Indexes => s_indexes;
}
