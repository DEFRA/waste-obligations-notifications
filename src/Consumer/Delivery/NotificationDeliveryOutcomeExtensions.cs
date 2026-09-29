namespace Defra.WasteObligations.Consumer.Delivery;

public static class NotificationDeliveryOutcomeExtensions
{
    public static string ToStorageValue(this NotificationDeliveryOutcome outcome) =>
        outcome switch
        {
            NotificationDeliveryOutcome.DeliverySuppressed => "delivery-suppressed",
            NotificationDeliveryOutcome.DeliveryAccepted => "delivery-accepted",
            NotificationDeliveryOutcome.DeliveryAbandoned => "delivery-abandoned",
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "Unsupported notification delivery outcome."
            ),
        };
}
