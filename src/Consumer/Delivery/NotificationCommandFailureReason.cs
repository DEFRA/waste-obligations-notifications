namespace Defra.WasteObligations.Consumer.Delivery;

public enum NotificationCommandFailureReason
{
    InvalidCommand,
    Conflict,
    ActiveClaim,
    NotifyRejected4xx,
    NotifyIndeterminate,
    StoreError,
    QueueError,
    OwnershipLost,
    ProcessingTimeout,
    UnexpectedError,
}
