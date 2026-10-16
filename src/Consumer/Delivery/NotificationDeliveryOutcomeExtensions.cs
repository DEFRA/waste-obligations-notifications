namespace Defra.WasteObligations.Consumer.Delivery;

public static class NotificationDeliveryOutcomeExtensions
{
    public static string ToStorageValue(this NotificationDeliveryOutcome outcome) =>
        outcome switch
        {
            NotificationDeliveryOutcome.DeliverySuppressed => NotificationDeliveryClassifications.DeliverySuppressed,
            NotificationDeliveryOutcome.DeliveryAccepted => NotificationDeliveryClassifications.DeliveryAccepted,
            NotificationDeliveryOutcome.DeliveryAbandoned => NotificationDeliveryClassifications.DeliveryAbandoned,
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "Unsupported notification delivery outcome."
            ),
        };
}
