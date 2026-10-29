using System.Text.Json;
using System.Text.Json.Serialization;

namespace Defra.WasteObligations.Consumer.Commands;

public sealed class NotificationSourceJsonConverter : JsonConverter<NotificationSource>
{
    public override NotificationSource? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.String
        )
            return null;
        var source = new NotificationSource(type.GetString()!, id.GetString()!);
        try
        {
            source.Validate();
            return source;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, NotificationSource value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        writer.WriteString("id", value.Id);
        writer.WriteEndObject();
    }
}
