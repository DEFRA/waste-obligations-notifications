namespace Defra.WasteObligations.Consumer.Delivery;

public sealed record NotificationDeliveryState(
    string Classification,
    DateTimeOffset? RecordedAtUtc,
    DateTimeOffset? LeaseExpiresAtUtc
);
