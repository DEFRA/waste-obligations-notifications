using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using Defra.WasteObligations.Consumer.Data.Migrations;
using MongoDB.Bson;
using MongoDB.Driver;
using MigrationVersion = AdaskoTheBeAsT.MongoDbMigrations.Abstractions.Version;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Fixtures;

[MigrationCollection("NotificationDeliveryRecord", MigrationDirection.Both)]
public sealed class OptionalStartupFixtureMigration : MongoMigration
{
    public override MigrationVersion Version => new(2, 0, 0);
    public override string Name => "Optional startup fixture";

    public override async Task UpAsync(MigrationContext context)
    {
        var control = context.Database.GetCollection<BsonDocument>("startup_fixture");
        while (
            await control.CountDocumentsAsync(
                new BsonDocument("_id", "hold-optional"),
                cancellationToken: context.CancellationToken
            ) > 0
        )
            await Task.Delay(20, context.CancellationToken);

        throw new InvalidOperationException("Optional fixture failure");
    }

    public override Task DownAsync(MigrationContext context) => throw new NotSupportedException();

    public override Task<bool> ValidateSchema(IMongoDatabase database, CancellationToken cancellationToken) =>
        Task.FromResult(true);
}
