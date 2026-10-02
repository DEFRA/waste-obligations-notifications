using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using Defra.WasteObligations.Consumer.Data.Migrations;
using MongoDB.Driver;
using MigrationVersion = AdaskoTheBeAsT.MongoDbMigrations.Abstractions.Version;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Fixtures;

[MigrationCollection("NotificationDeliveryRecord", MigrationDirection.Both)]
public sealed class CriticalStartupFixtureMigration : MongoMigration
{
    public override bool Critical => true;
    public override MigrationVersion Version => new(1, 0, 0);
    public override string Name => "Critical startup fixture";

    public override Task UpAsync(MigrationContext context) => new NotificationDeliveryRecordIndexes().UpAsync(context);

    public override Task DownAsync(MigrationContext context) =>
        new NotificationDeliveryRecordIndexes().DownAsync(context);

    public override Task<bool> ValidateSchema(IMongoDatabase database, CancellationToken cancellationToken) =>
        new NotificationDeliveryRecordIndexes().ValidateSchema(database, cancellationToken);
}
