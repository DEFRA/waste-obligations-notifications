using Defra.WasteObligations.Consumer.Commands;
using MongoDB.Bson;

namespace Defra.WasteObligations.Consumer.Data;

public static class NotificationSourceReader
{
    public static NotificationSource? Read(BsonDocument record)
    {
        if (
            !record.TryGetValue("source", out var value)
            || value is not BsonDocument source
            || !source.TryGetValue("type", out var type)
            || !type.IsString
            || !source.TryGetValue("id", out var id)
            || !id.IsString
        )
            return null;
        var result = new NotificationSource(type.AsString, id.AsString);
        try
        {
            result.Validate();
            return result;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
