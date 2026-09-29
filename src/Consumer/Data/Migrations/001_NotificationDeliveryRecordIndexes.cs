using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using Defra.WasteObligations.Consumer.Delivery;
using MongoDB.Driver;
using MigrationVersion = AdaskoTheBeAsT.MongoDbMigrations.Abstractions.Version;

namespace Defra.WasteObligations.Consumer.Data.Migrations;

[MigrationCollection(MongoNotificationDeliveryRecordStore.CollectionName, MigrationDirection.Both)]
public sealed class NotificationDeliveryRecordIndexes : MongoMigration
{
    private const string NotificationKeyIndexName = "notificationKey_unique";

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
}
