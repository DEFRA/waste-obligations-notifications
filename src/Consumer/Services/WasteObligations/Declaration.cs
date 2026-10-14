using System.Text.Json.Serialization;

namespace Defra.WasteObligations.Consumer.Services.WasteObligations;

public sealed record Declaration(
    string? Id,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] int Version,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] int ObligationYear,
    DeclarationOrganisation? Organisation,
    IReadOnlyList<DeclarationAudit>? Audit
);
