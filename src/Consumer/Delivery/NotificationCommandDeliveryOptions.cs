using System.ComponentModel.DataAnnotations;

namespace Defra.WasteObligations.Consumer.Delivery;

public sealed record NotificationCommandDeliveryOptions
{
    public const string SectionName = "NotificationCommandDelivery";

    [Required]
    public required string QueueUrl { get; init; }

    public bool ProcessingEnabled { get; init; }

    [Required]
    public required string EmailDeliveryCutoverUtc { get; init; }

    [Required]
    public required string EvidenceDigestSecret { get; init; }

    [Required]
    public required string RecipientLaneSecret { get; init; }

    [Range(1, 1)]
    public int BatchSize { get; init; } = 1;

    [Range(0, 20)]
    public int WaitTimeSeconds { get; init; } = 20;

    [Range(1, 300)]
    public int PollIntervalSeconds { get; init; } = 15;
}
