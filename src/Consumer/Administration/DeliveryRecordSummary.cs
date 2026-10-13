namespace Defra.WasteObligations.Consumer.Administration;

public sealed record DeliveryRecordSummary(
    string NotificationKey,
    string? ResendOfNotificationKey,
    string NotificationType,
    string Outcome,
    DateTime RecordedAtUtc,
    DateTime ActionOccurredAtUtc,
    DateTime? AcceptedAtUtc,
    DateTime? LeaseExpiresAtUtc,
    string? NotifyNotificationId,
    string? NotifyReference,
    string? TemplateId,
    int? TemplateVersion,
    DeliveryRecordLinks? Links
);
