using System.ComponentModel.DataAnnotations;
using Defra.WasteObligations.Consumer.Commands;

namespace Defra.WasteObligations.Consumer.Delivery;

public sealed record NotificationCommandDeliveryOptions
{
    public const string SectionName = "NotificationCommandDelivery";

    [Required]
    public required string QueueUrl { get; init; }

    public bool ProcessingEnabled { get; init; }

    public string? EmailDeliveryCutoverUtc { get; init; }

    [Required]
    public required string EvidenceDigestSecret { get; init; }

    [Required]
    public required string RecipientLaneSecret { get; init; }

    [Range(1, 1)]
    public int BatchSize { get; init; } = 1;

    [Range(0, 20)]
    public int WaitTimeSeconds { get; init; } = 20;

    [Range(1, 300)]
    public int ReceiveTimeoutSeconds { get; init; } = 30;

    [Range(1, 300)]
    public int PollIntervalSeconds { get; init; } = 15;

    public bool TryReadCutover(out DateTimeOffset? cutover)
    {
        cutover = null;
        if (EmailDeliveryCutoverUtc is null)
            return true;
        if (!UtcTimestamp.TryParse(EmailDeliveryCutoverUtc, out var parsed))
            return false;
        cutover = parsed;

        return true;
    }

    internal static bool IsSecretConfigured(string? secret) =>
        !string.IsNullOrWhiteSpace(secret) && !secret.StartsWith("set-automatically", StringComparison.Ordinal);
}
