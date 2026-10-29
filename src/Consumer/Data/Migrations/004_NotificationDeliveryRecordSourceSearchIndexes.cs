using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using Defra.WasteObligations.Consumer.Delivery;
using MongoDB.Bson;
using MigrationVersion = AdaskoTheBeAsT.MongoDbMigrations.Abstractions.Version;

namespace Defra.WasteObligations.Consumer.Data.Migrations;

[MigrationCollection(MongoNotificationDeliveryRecordStore.CollectionName, MigrationDirection.Both)]
public sealed class NotificationDeliveryRecordSourceSearchIndexes : NotificationDeliveryRecordSearchMigration
{
    private const string RecordedAtField = "recordedAtUtc";
    private static readonly (string Name, BsonDocument Keys)[] s_indexes =
    [
        (
            "recipient_recordedAtUtc_id_desc",
            new BsonDocument
            {
                { "recipient", 1 },
                { RecordedAtField, -1 },
                { "_id", -1 },
            }
        ),
        (
            "source_recordedAtUtc_id_desc",
            new BsonDocument
            {
                { "source.type", 1 },
                { "source.id", 1 },
                { RecordedAtField, -1 },
                { "_id", -1 },
            }
        ),
    ];

    public override MigrationVersion Version => new(4, 0, 0);

    public override string Name => "004 - NotificationDeliveryRecord source search indexes";

    protected override IReadOnlyList<(string Name, BsonDocument Keys)> Indexes => s_indexes;
}
