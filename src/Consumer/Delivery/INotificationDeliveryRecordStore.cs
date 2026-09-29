using Defra.WasteObligations.Consumer.Commands;

namespace Defra.WasteObligations.Consumer.Delivery;

public interface INotificationDeliveryRecordStore
{
    Task<SuppressionClaimResult> RecordSuppression(NotificationCommand command, CancellationToken cancellationToken);
}
