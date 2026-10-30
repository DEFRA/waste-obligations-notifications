using MongoDB.Bson.Serialization.Attributes;

namespace Defra.WasteObligations.Consumer.Data.Entities;

public sealed record ScheduledCampaignRun
{
    public required string Id { get; init; }

    public required string Campaign { get; init; }

    public required int ObligationYear { get; init; }

    public required ScheduledCampaignRunStatus Status { get; init; }

    public int ScanCount { get; init; }

    [BsonIgnoreIfNull]
    public DateTime? LastScanStartedAtUtc { get; init; }

    [BsonIgnoreIfNull]
    public DateTime? LastScanFinishedAtUtc { get; init; }

    [BsonIgnoreIfNull]
    public DateTime? CompletedAtUtc { get; init; }

    public int OrganisationsProcessed { get; init; }

    public int CommandsPublished { get; init; }

    public int OrganisationsSkipped { get; init; }

    public int RetryableFailures { get; init; }
}
