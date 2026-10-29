namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed class ScheduledCampaignLeaseKeeper(
    IScheduledCampaignLeaseService leases,
    TimeProvider timeProvider,
    ILogger<ScheduledCampaignLeaseKeeper> logger
)
{
    public async Task<ScheduledCampaignLeaseHold?> TryHold(
        string leaseId,
        TimeSpan leaseDuration,
        TimeSpan renewalInterval,
        CancellationToken cancellationToken
    )
    {
        var requestStartedAt = timeProvider.GetTimestamp();
        if (!await leases.TryAcquire(leaseId, leaseDuration, cancellationToken))
            return null;

        var hold = new ScheduledCampaignLeaseHold(
            leases,
            timeProvider,
            logger,
            leaseId,
            leaseDuration,
            renewalInterval,
            requestStartedAt,
            cancellationToken
        );
        if (!hold.IsLost)
            return hold;

        await hold.DisposeAsync();

        return null;
    }
}
