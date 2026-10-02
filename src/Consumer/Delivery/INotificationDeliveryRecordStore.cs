using Defra.WasteObligations.Consumer.Commands;

namespace Defra.WasteObligations.Consumer.Delivery;

public interface INotificationDeliveryRecordStore
{
    Task<SuppressionClaimResult?> GetSuppression(NotificationCommand command, CancellationToken cancellationToken);

    Task<SuppressionClaimResult> RecordSuppression(NotificationCommand command, CancellationToken cancellationToken);
}
