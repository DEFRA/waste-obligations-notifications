using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using Defra.WasteObligations.Consumer.Delivery;
using MongoDB.Bson;
using MigrationVersion = AdaskoTheBeAsT.MongoDbMigrations.Abstractions.Version;

namespace Defra.WasteObligations.Consumer.Data.Migrations;

[MigrationCollection(MongoNotificationDeliveryRecordStore.CollectionName, MigrationDirection.Both)]
public sealed class NotificationDeliveryRecordCorrelationSearchIndex : NotificationDeliveryRecordSearchMigration
{
    private const string RecordedAtField = "recordedAtUtc";
    private static readonly (string Name, BsonDocument Keys)[] s_indexes =
    [
        (
            "correlationId_recordedAtUtc_id_desc",
            new BsonDocument
            {
                { "correlationId", 1 },
                { RecordedAtField, -1 },
                { "_id", -1 },
            }
        ),
    ];

    public override MigrationVersion Version => new(5, 0, 0);

    public override string Name => "005 - NotificationDeliveryRecord correlation search index";

    protected override IReadOnlyList<(string Name, BsonDocument Keys)> Indexes => s_indexes;
}
