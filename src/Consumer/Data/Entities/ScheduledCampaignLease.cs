namespace Defra.WasteObligations.Consumer.Data.Entities;

public sealed record ScheduledCampaignLease
{
    public const string CollectionName = "_scheduled_campaign_leases";

    public required string Id { get; init; }

    public required string Owner { get; init; }

    public required DateTime ExpiresAt { get; init; }
}
