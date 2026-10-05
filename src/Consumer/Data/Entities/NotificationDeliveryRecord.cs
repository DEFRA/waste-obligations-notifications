using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Defra.WasteObligations.Consumer.Data.Entities;

public sealed record NotificationDeliveryRecord
{
    [BsonId]
    public ObjectId Id { get; init; }

    public required string NotificationKey { get; init; }

    public required string ImmutableFields { get; init; }

    public required string Recipient { get; init; }

    public required string NotificationType { get; init; }

    public required DateTime ActionOccurredAtUtc { get; init; }

    public required string Outcome { get; init; }

    public required DateTime RecordedAtUtc { get; init; }
}
