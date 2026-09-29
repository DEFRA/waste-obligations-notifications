using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Defra.WasteObligations.Consumer.Delivery;

public sealed record NotificationDeliveryRecord
{
    [BsonId]
    public ObjectId Id { get; init; }

    [BsonElement("notificationKey")]
    public required string NotificationKey { get; init; }

    [BsonElement("immutableFields")]
    public required string ImmutableFields { get; init; }

    [BsonElement("recipient")]
    public required string Recipient { get; init; }

    [BsonElement("notificationType")]
    public required string NotificationType { get; init; }

    [BsonElement("actionOccurredAtUtc")]
    public required DateTime ActionOccurredAtUtc { get; init; }

    [BsonElement("outcome")]
    public required string Outcome { get; init; }

    [BsonElement("recordedAtUtc")]
    public required DateTime RecordedAtUtc { get; init; }
}
