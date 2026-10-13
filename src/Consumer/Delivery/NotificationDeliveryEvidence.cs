namespace Defra.WasteObligations.Consumer.Delivery;

public sealed record NotificationDeliveryEvidence(
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
    int? TemplateVersion
);
