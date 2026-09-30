namespace Defra.WasteObligations.Consumer.Data;

public interface IMongoMigrationRunner
{
    Task<bool> CheckReadiness(CancellationToken cancellationToken);

    Task Run(CancellationToken cancellationToken);
}
