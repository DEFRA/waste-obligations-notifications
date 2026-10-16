namespace Defra.WasteObligations.Consumer.Administration;

public sealed record CommandDlqResendResult(string SourceNotificationKey, string ReplacementNotificationKey)
{
    public string PublicationStatus { get; } = "confirmed";
    public bool SourceMessageRemoved { get; } = true;
    public CommandDlqResendLinks Links =>
        new(
            "/admin/notification-commands/delivery-records?notificationKey="
                + Uri.EscapeDataString(SourceNotificationKey),
            "/admin/notification-commands/delivery-records?notificationKey="
                + Uri.EscapeDataString(ReplacementNotificationKey)
        );
}
