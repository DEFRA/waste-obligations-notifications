using Defra.WasteObligations.Consumer.Commands;

namespace Defra.WasteObligations.Consumer.Delivery;

public interface INotificationDeliveryRecordStore
{
    Task<NotificationDeliveryState> Inspect(NotificationCommand command, CancellationToken cancellationToken);

    Task<SuppressionClaimResult> RecordSuppression(NotificationCommand command, CancellationToken cancellationToken);

    Task<DeliveryClaimResult> Claim(
        NotificationCommand command,
        string attemptOwner,
        int leaseDurationSeconds,
        CancellationToken cancellationToken
    );

    Task<bool> RecordAcceptance(
        NotificationCommand command,
        string attemptOwner,
        NotifyAcceptance acceptance,
        CancellationToken cancellationToken
    );
}
