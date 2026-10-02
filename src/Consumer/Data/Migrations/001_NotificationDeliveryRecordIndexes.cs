using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using Defra.WasteObligations.Consumer.Data.Entities;
using Defra.WasteObligations.Consumer.Delivery;
using MongoDB.Bson;
using MongoDB.Driver;
using MigrationVersion = AdaskoTheBeAsT.MongoDbMigrations.Abstractions.Version;

namespace Defra.WasteObligations.Consumer.Data.Migrations;

[MigrationCollection(MongoNotificationDeliveryRecordStore.CollectionName, MigrationDirection.Both)]
public sealed class NotificationDeliveryRecordIndexes : MongoMigration
{
    internal const string NotificationKeyIndexName = "notificationKey_unique";

    public override MigrationVersion Version => new(1, 0, 0);

    public override string Name => "001 - NotificationDeliveryRecord indexes";

    public override Task UpAsync(MigrationContext context) =>
        CreateIndex(
            context,
            MongoNotificationDeliveryRecordStore.CollectionName,
            NotificationKeyIndexName,
            Builders<NotificationDeliveryRecord>.IndexKeys.Ascending(record => record.NotificationKey),
            unique: true
        );

    public override Task DownAsync(MigrationContext context) =>
        DropIndex<NotificationDeliveryRecord>(
            context,
            MongoNotificationDeliveryRecordStore.CollectionName,
            NotificationKeyIndexName
        );

    public override async Task<bool> ValidateSchema(IMongoDatabase database, CancellationToken cancellationToken)
    {
        var records = database.GetCollection<BsonDocument>(MongoNotificationDeliveryRecordStore.CollectionName);
        using var cursor = await records.Indexes.ListAsync(cancellationToken);
        var indexes = await cursor.ToListAsync(cancellationToken);
        var index = indexes.FirstOrDefault(index => index.GetValue("name", "") == NotificationKeyIndexName);

        if (
            index is null
            || index.GetValue("unique", false) != BsonBoolean.True
            || !index.GetValue("key", new BsonDocument()).Equals(new BsonDocument("notificationKey", 1))
        )
            return false;

        return true;
    }
}
