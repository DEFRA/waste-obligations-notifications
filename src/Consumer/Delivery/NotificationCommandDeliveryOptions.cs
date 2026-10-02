using System.ComponentModel.DataAnnotations;
using Defra.WasteObligations.Consumer.Commands;

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

    [Range(1, 43200)]
    public int VisibilityTimeoutSeconds { get; init; } = 120;

    [Range(1, 43200)]
    public int CommandLeaseSeconds { get; init; } = 90;

    [Range(1, 300)]
    public int ClaimTimeoutSeconds { get; init; } = 5;

    [Range(1, 300)]
    public int NotifyTimeoutSeconds { get; init; } = 60;

    [Range(1, 300)]
    public int AcceptanceTimeoutSeconds { get; init; } = 10;

    [Range(1, 300)]
    public int DeleteTimeoutSeconds { get; init; } = 5;

    [Range(1, 300)]
    public int SafetyHeadroomSeconds { get; init; } = 10;

    public int CompletionBudgetSeconds =>
        NotifyTimeoutSeconds + AcceptanceTimeoutSeconds + DeleteTimeoutSeconds + SafetyHeadroomSeconds;

    public bool HasValidProcessingBudget =>
        CommandLeaseSeconds >= ClaimTimeoutSeconds + CompletionBudgetSeconds
        && VisibilityTimeoutSeconds >= ReceiveTimeoutSeconds + ClaimTimeoutSeconds + CompletionBudgetSeconds;

    [MaxLength(32)]
    public string[] DiagnosticNotificationTypes { get; init; } = [];

    public string GetDiagnosticNotificationType(string notificationType) =>
        DiagnosticNotificationTypes.FirstOrDefault(label => IsDiagnosticLabel(label) && label == notificationType)
        ?? "other";

    internal static bool IsDiagnosticLabel(string? label) =>
        label is { Length: > 0 and <= 64 }
        && label.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    public bool TryReadCutover(out DateTimeOffset cutover) =>
        UtcTimestamp.TryParse(EmailDeliveryCutoverUtc, out cutover);

    internal static bool IsSecretConfigured(string? secret) =>
        !string.IsNullOrWhiteSpace(secret) && !secret.StartsWith("set-automatically", StringComparison.Ordinal);
}
