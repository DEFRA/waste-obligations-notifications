using System.Text.Json;

namespace Defra.WasteObligations.Consumer.Administration;

public sealed record CommandDlqInspection(
    string? IdempotencyKey,
    string? NotificationType,
    DateTimeOffset? ActionOccurredAtUtc,
    DateTimeOffset? SentAtUtc,
    int? ReceiveCount,
    string FailureClassification,
    string? RecipientDigest,
    DateTimeOffset? RecordedAtUtc,
    DateTimeOffset? LeaseExpiresAtUtc,
    string? SelectionToken,
    string MessageId,
    int? SchemaVersion,
    string? EmailAddress,
    string? TemplateId,
    JsonElement? Personalisation
);
