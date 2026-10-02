using AdaskoTheBeAsT.MongoDbMigrations;
using Defra.WasteObligations.Consumer.Data.Migrations;
using MongoDB.Bson;
using MongoDB.Driver;
using MigrationVersion = AdaskoTheBeAsT.MongoDbMigrations.Abstractions.Version;

namespace Defra.WasteObligations.Consumer.Data;

public sealed class MongoMigrationRunner(
    IMongoDatabase database,
    ILogger<MongoMigrationRunner> logger,
    MongoMigrationCompletion completion
) : IMongoMigrationRunner
{
    private static readonly MongoMigration s_requiredMigration = typeof(MongoMigration)
        .Assembly.GetTypes()
        .Where(type => type.IsAssignableTo(typeof(MongoMigration)) && !type.IsAbstract)
        .Select(type => (MongoMigration)Activator.CreateInstance(type)!)
        .MaxBy(migration => migration.Version)!;

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
            || new MigrationVersion(latest.GetValue("v", "0.0.0").AsString) < s_requiredMigration.Version
        )
            return false;

        if (!await s_requiredMigration.ValidateSchema(database, cancellationToken))
            return false;

        completion.MarkCompleted();

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
            throw new InvalidOperationException("Mongo migration history or current schema validation is incomplete.");
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
