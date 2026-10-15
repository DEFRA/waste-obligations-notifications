namespace Defra.WasteObligations.Consumer.Delivery;

public static class NotificationDeliveryClassifications
{
    public const string DeliveryAccepted = "delivery-accepted";
    public const string ActiveClaim = "active-claim";
    public const string ExpiredClaim = "expired-claim";
    public const string Unrecorded = "unrecorded";
    public const string DeliverySuppressed = "delivery-suppressed";
    public const string DeliveryAbandoned = "delivery-abandoned";
    public const string ImmutableConflict = "immutable-conflict";
    public const string UnknownDeliveryState = "unknown-delivery-state";
    public const string InvalidOrUnsupportedCommand = "invalid-or-unsupported-command";
}
