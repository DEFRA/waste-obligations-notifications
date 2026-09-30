using AdaskoTheBeAsT.MongoDbMigrations;
using Defra.WasteObligations.Consumer.Data.Migrations;
using Defra.WasteObligations.Consumer.Delivery;
using MongoDB.Bson;
using MongoDB.Driver;
using MigrationVersion = AdaskoTheBeAsT.MongoDbMigrations.Abstractions.Version;

namespace Defra.WasteObligations.Consumer.Data;

public sealed class MongoMigrationRunner(
    IMongoDatabase database,
    ILogger<MongoMigrationRunner> logger,
    MongoMigrationReadiness readiness
) : IMongoMigrationRunner
{
    private static readonly MigrationVersion s_requiredVersion = typeof(MongoMigration)
        .Assembly.GetTypes()
        .Where(type => type.IsAssignableTo(typeof(MongoMigration)) && !type.IsAbstract)
        .Select(type => ((MongoMigration)Activator.CreateInstance(type)!).Version)
        .Max();

    public async Task<bool> CheckReadiness(CancellationToken cancellationToken)
    {
        // Read the engine's history without registering its serializers before the engine starts.
        var latest = await database
            .GetCollection<BsonDocument>("_migrations")
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Descending("applied"))
            .FirstOrDefaultAsync(cancellationToken);

        if (
            latest is null
            || latest.GetValue("d", false) != BsonBoolean.True
            || new MigrationVersion(latest.GetValue("v", "0.0.0").AsString) < s_requiredVersion
        )
            return false;

        var records = database.GetCollection<BsonDocument>(MongoNotificationDeliveryRecordStore.CollectionName);
        using var cursor = await records.Indexes.ListAsync(cancellationToken);
        var indexes = await cursor.ToListAsync(cancellationToken);
        var index = indexes.FirstOrDefault(index =>
            index.GetValue("name", "") == NotificationDeliveryRecordIndexes.NotificationKeyIndexName
        );

        if (
            index is null
            || index.GetValue("unique", false) != BsonBoolean.True
            || !index.GetValue("key", new BsonDocument()).Equals(new BsonDocument("notificationKey", 1))
        )
            return false;

        readiness.MarkCompleted();

        return true;
    }

    public async Task Run(CancellationToken cancellationToken)
    {
        using var engine = new MigrationEngineBuilder().UseDatabase(
            database.Client,
            database.DatabaseNamespace.DatabaseName
        );

        var result = await engine
            .UseAssemblyOfType<NotificationDeliveryRecordIndexes>()
            .UseSchemeValidation(false)
            .UseAfterMigration(
                (migration, success) =>
                {
                    if (success && logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation(
                            "Mongo migration {MigrationName} version {MigrationVersion} completed.",
                            migration.Name,
                            migration.Version
                        );
                    }
                    else if (!success && logger.IsEnabled(LogLevel.Error))
                    {
                        logger.LogError(
                            "Mongo migration {MigrationName} version {MigrationVersion} failed.",
                            migration.Name,
                            migration.Version
                        );
                    }
                }
            )
            .RunAsync(cancellationToken);

        if (!result.Success)
        {
            throw new InvalidOperationException("Mongo migrations did not complete successfully.");
        }

        if (!await CheckReadiness(cancellationToken))
        {
            throw new InvalidOperationException("Mongo migration history or the required unique index is incomplete.");
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Mongo migrations completed. Current version is {CurrentVersion}. Applied {AppliedMigrationCount} migration(s).",
                result.CurrentVersion,
                result.InterimSteps.Count
            );
        }
    }
}
