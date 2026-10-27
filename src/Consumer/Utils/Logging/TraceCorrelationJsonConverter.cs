using System.Text.Json;
using System.Text.Json.Serialization;

namespace Defra.WasteObligations.Consumer.Utils.Logging;

public sealed class TraceCorrelationJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return TraceCorrelation.Read(reader.GetString());

        reader.Skip();

        return null;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(TraceCorrelation.Read(value));
}
