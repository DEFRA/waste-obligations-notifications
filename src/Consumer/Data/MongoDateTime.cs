namespace Defra.WasteObligations.Consumer.Data;

public static class MongoDateTime
{
    public static DateTimeOffset TruncateToMilliseconds(DateTimeOffset timestamp) =>
        new(timestamp.Ticks - timestamp.Ticks % TimeSpan.TicksPerMillisecond, timestamp.Offset);
}
