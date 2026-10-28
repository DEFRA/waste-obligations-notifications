using MongoDB.Bson.Serialization.Attributes;

namespace Defra.WasteObligations.Consumer.Commands;

[BsonNoId]
public sealed record NotificationSource(string Type, string Id)
{
    public void Validate()
    {
        if (!Valid(Type, 64) || !Valid(Id, 128))
            throw new InvalidDataException("Notification source must contain safe identifiers.");
    }

    private static bool Valid(string? value, int maximum) =>
        value is { Length: > 0 }
        && value.Length <= maximum
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
