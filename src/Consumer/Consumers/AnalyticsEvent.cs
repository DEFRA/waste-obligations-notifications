using System.Text.Json;

namespace Defra.WasteObligations.Consumer.Consumers;

public sealed record AnalyticsEvent(
    string EventId,
    string EntityId,
    string? Entity,
    string? EventType,
    string? SchemaVersion,
    int? Version,
    Guid? OrganisationId
)
{
    public bool IsSubmission => Entity == "compliance_declaration" && EventType == "submission.created";

    public static AnalyticsEvent Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var organisation = Object(Object(root, "after"), "organisation");
        var organisationId = String(organisation, "id");

        return new AnalyticsEvent(
            RequiredString(root, "eventId"),
            RequiredString(root, "entityId"),
            String(root, "entity"),
            String(root, "eventType"),
            String(root, "schemaVersion"),
            root.TryGetProperty("version", out var version)
            && version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out var value)
                ? value
                : null,
            Guid.TryParse(organisationId, out var id) ? id : null
        );
    }

    private static JsonElement Object(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RequiredString(JsonElement element, string name)
    {
        var value = String(element, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Analytics event is missing required property '{name}'.");

        return value;
    }
}
