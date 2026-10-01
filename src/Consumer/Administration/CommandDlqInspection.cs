namespace Defra.WasteObligations.Consumer.Administration;

public sealed record CommandDlqInspection(
    string? IdempotencyKey,
    string? NotificationType,
    DateTimeOffset? ActionOccurredAtUtc,
    DateTimeOffset? SentAtUtc,
    int? ReceiveCount,
    string FailureClassification,
    string FailureDetails,
    string? RecipientDigest,
    DateTimeOffset? RecordedAtUtc,
    DateTimeOffset? LeaseExpiresAtUtc,
    string? SelectionToken
);
