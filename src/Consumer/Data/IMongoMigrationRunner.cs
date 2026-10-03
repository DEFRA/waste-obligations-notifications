namespace Defra.WasteObligations.Consumer.Data;

public interface IMongoMigrationRunner
{
    Task<bool> CheckCompletion(CancellationToken cancellationToken);

    Task Run(CancellationToken cancellationToken);
}
