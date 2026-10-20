using System.Text.Json;

namespace Defra.WasteObligations.Consumer.Consumers;

public sealed record AnalyticsEvent(
    string EventId,
    string EntityId,
    string? Entity,
    string? EventType,
    string? SchemaVersion,
    int? Version,
    Guid? OrganisationId,
    string? BeforeStatus = null,
    string? AfterStatus = null
)
{
    private const string SchemaPrefix = "compliance_declaration_v";
    private static readonly System.Version s_minimumSchema = new(1, 0);

    public bool IsSubmission => Entity == "compliance_declaration" && EventType == "submission.created";

    public bool IsCancellation =>
        Entity == "compliance_declaration"
        && EventType == "submission.amended"
        && BeforeStatus is "Submitted" or "Accepted"
        && AfterStatus == "Cancelled";

    public bool SupportsSubmissionSchema =>
        SchemaVersion is { } schema
        && schema.StartsWith(SchemaPrefix, StringComparison.Ordinal)
        && System.Version.TryParse(schema[SchemaPrefix.Length..], out var version)
        && version >= s_minimumSchema;

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
            Guid.TryParse(organisationId, out var id) ? id : null,
            String(Object(root, "before"), "status"),
            String(Object(root, "after"), "status")
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
