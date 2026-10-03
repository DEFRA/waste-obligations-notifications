using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using Defra.WasteObligations.Consumer.Data.Migrations;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.MigrationFixtures;

public abstract class TimedFixtureMigration : MongoMigration
{
    protected abstract string Stage { get; }

    public override string Name => Stage;

    public override async Task UpAsync(MigrationContext context)
    {
        var control = context.Database.GetCollection<BsonDocument>("timing_fixture");
        await control.ReplaceOneAsync(
            new BsonDocument("_id", Stage + "-started"),
            new BsonDocument("_id", Stage + "-started"),
            new ReplaceOptions { IsUpsert = true },
            context.CancellationToken
        );
        var ignoreCancellation = await control
            .Find(new BsonDocument("_id", "ignore-cancellation"))
            .AnyAsync(context.CancellationToken);
        var waitToken = ignoreCancellation ? CancellationToken.None : context.CancellationToken;
        var cancellationRecorded = false;
        while (await control.Find(new BsonDocument("_id", "hold-" + Stage)).AnyAsync(waitToken))
        {
            if (context.CancellationToken.IsCancellationRequested && !cancellationRecorded)
            {
                await control.InsertOneAsync(
                    new BsonDocument("_id", Stage + "-cancelled"),
                    cancellationToken: CancellationToken.None
                );
                cancellationRecorded = true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), waitToken);
        }
        context.CancellationToken.ThrowIfCancellationRequested();
        await control.ReplaceOneAsync(
            new BsonDocument("_id", Stage),
            new BsonDocument("_id", Stage),
            new ReplaceOptions { IsUpsert = true },
            context.CancellationToken
        );
    }

    public override Task DownAsync(MigrationContext context) =>
        context
            .Database.GetCollection<BsonDocument>("timing_fixture")
            .DeleteOneAsync(new BsonDocument("_id", Stage), context.CancellationToken);

    public override Task<bool> ValidateSchema(IMongoDatabase database, CancellationToken cancellationToken) =>
        database
            .GetCollection<BsonDocument>("timing_fixture")
            .Find(new BsonDocument("_id", Stage))
            .AnyAsync(cancellationToken);
}
