using AdaskoTheBeAsT.MongoDbMigrations;
using Defra.WasteObligations.Consumer.Data.Migrations;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.Data;

public sealed class MongoMigrationRunner(
    IMongoDatabase database,
    ILogger<MongoMigrationRunner> logger,
    MongoMigrationReadiness readiness
) : IMongoMigrationRunner
{
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
                    if (success)
                    {
                        logger.LogInformation(
                            "Mongo migration {MigrationName} version {MigrationVersion} completed.",
                            migration.Name,
                            migration.Version
                        );
                    }
                    else
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

        readiness.MarkCompleted();

        logger.LogInformation(
            "Mongo migrations completed. Current version is {CurrentVersion}. Applied {AppliedMigrationCount} migration(s).",
            result.CurrentVersion,
            result.InterimSteps.Count
        );
    }
}
