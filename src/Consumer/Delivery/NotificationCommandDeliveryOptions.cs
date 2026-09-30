using System.ComponentModel.DataAnnotations;
using System.Globalization;

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
    public int ReceiveTimeoutSeconds { get; init; } = 30;

    [Range(1, 300)]
    public int PollIntervalSeconds { get; init; } = 15;

    public bool TryReadCutover(out DateTimeOffset cutover) =>
        DateTimeOffset.TryParse(EmailDeliveryCutoverUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out cutover)
        && cutover.Offset == TimeSpan.Zero;

    internal static bool IsSecretConfigured(string? secret) =>
        !string.IsNullOrWhiteSpace(secret) && !secret.StartsWith("set-automatically", StringComparison.Ordinal);
}
