namespace Defra.WasteObligations.Consumer.Scheduled;

public interface IScheduledCampaignLeaseService
{
    Task<bool> TryAcquire(string leaseId, TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task<bool> TryRenew(string leaseId, TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task Release(string leaseId, CancellationToken cancellationToken);
}
