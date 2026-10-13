using System.Text.Json;

namespace Defra.WasteObligations.Consumer.Submission;

public sealed record SubmissionEvent(string EventId, string EntityId, Guid OrganisationId, int Version)
{
    public string DeclarationId => EntityId[5..];

    public static SubmissionEvent? Read(JsonElement root, string eventId, string entityId)
    {
        if (
            ReadString(root, "entity") != "compliance_declaration"
            || ReadString(root, "eventType") != "submission.created"
        )
            return null;
        if (
            !IsEventId(eventId)
            || !IsEntityId(entityId)
            || ReadString(root, "schemaVersion") != "compliance_declaration_v1.3"
        )
            throw new SubmissionProcessingException("invalid-event");
        if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number <= 0)
            throw new SubmissionProcessingException("invalid-event");
        if (
            !root.TryGetProperty("after", out var after)
            || after.ValueKind != JsonValueKind.Object
            || !after.TryGetProperty("organisation", out var organisation)
            || !Guid.TryParseExact(ReadString(organisation, "id"), "D", out var organisationId)
            || organisationId == Guid.Empty
        )
            throw new SubmissionProcessingException("invalid-event");

        return new SubmissionEvent(eventId, entityId, organisationId, number);
    }

    public static bool IsEventId(string? value) =>
        value is { Length: 26 }
        && value[0] <= '7'
        && value.All(character => "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(character));

    public static bool IsEntityId(string? value) =>
        value is { Length: 29 } && value.StartsWith("cdec_", StringComparison.Ordinal) && IsDeclarationId(value[5..]);

    public static bool IsDeclarationId(string? value) =>
        value is { Length: 24 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
