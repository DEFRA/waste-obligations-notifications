namespace Defra.WasteObligations.Consumer.Administration;

public sealed record DeliveryRecordSummary(
    string NotificationKey,
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
    string? ResendOfNotificationKey = null
)
{
    public DeliveryRecordParentLinks? Links =>
        ResendOfNotificationKey is { } parent
            ? new("/admin/notification-commands/delivery-records?notificationKey=" + Uri.EscapeDataString(parent))
            : null;
}
