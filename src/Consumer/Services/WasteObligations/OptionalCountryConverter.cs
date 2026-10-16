using System.Text.Json;
using System.Text.Json.Serialization;

namespace Defra.WasteObligations.Consumer.Services.WasteObligations;

// Country is optional upstream; only the exact Welsh string changes language selection.
public sealed class OptionalCountryConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString();

        using var ignored = JsonDocument.ParseValue(ref reader);

        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
