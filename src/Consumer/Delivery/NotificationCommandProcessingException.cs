namespace Defra.WasteObligations.Consumer.Delivery;

public sealed class NotificationCommandProcessingException(
    NotificationCommandFailureReason reason,
    Exception? cause = null
) : InvalidOperationException("Notification command processing failed.")
{
    public NotificationCommandFailureReason Reason { get; } = reason;

    // Keep only type identity; dependency messages, inner exceptions and responses can contain private data.
    public string ExceptionType { get; } =
        cause is NotificationCommandProcessingException failure
            ? failure.ExceptionType
            : cause?.GetBaseException().GetType().Name ?? nameof(NotificationCommandProcessingException);

    public string DiagnosticReason =>
        Reason switch
        {
            NotificationCommandFailureReason.InvalidCommand => "invalid-command",
            NotificationCommandFailureReason.Conflict => "conflict",
            NotificationCommandFailureReason.ActiveClaim => "active-claim",
            NotificationCommandFailureReason.NotifyRejected4xx => "notify-rejected-4xx",
            NotificationCommandFailureReason.NotifyIndeterminate => "notify-indeterminate",
            NotificationCommandFailureReason.StoreError => "store-error",
            NotificationCommandFailureReason.QueueError => "queue-error",
            NotificationCommandFailureReason.OwnershipLost => "ownership-lost",
            NotificationCommandFailureReason.ProcessingTimeout => "processing-timeout",
            _ => "unexpected-error",
        };
}
