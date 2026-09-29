namespace Defra.WasteObligations.Consumer.Data;

public interface IMongoMigrationRunner
{
    Task Run(CancellationToken cancellationToken);
}
