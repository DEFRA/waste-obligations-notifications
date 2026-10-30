namespace Defra.WasteObligations.Consumer.Scheduled;

internal sealed record ScheduledCampaignLeaseRequest(
    string LeaseId,
    TimeSpan LeaseDuration,
    TimeSpan RenewalInterval,
    long AcquireRequestStartedAt,
    CancellationToken StoppingToken
);
