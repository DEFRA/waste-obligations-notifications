namespace Defra.WasteObligations.Consumer.Data;

public interface IMongoMigrationLeaseService
{
    string InstanceId { get; }

    Task<bool> TryAcquire(TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task<bool> TryRenew(TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task Release(CancellationToken cancellationToken);
}
