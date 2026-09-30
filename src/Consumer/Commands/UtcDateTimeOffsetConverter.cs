using System.Text.Json;
using System.Text.Json.Serialization;
using Defra.WasteObligations.Consumer.Data;

namespace Defra.WasteObligations.Consumer.Commands;

public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !UtcTimestamp.TryParse(reader.GetString(), out var timestamp))
        {
            throw new JsonException("Notification command actionOccurredAtUtc must include an explicit UTC offset.");
        }

        return timestamp;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(MongoDateTime.TruncateToMilliseconds(value));
}
