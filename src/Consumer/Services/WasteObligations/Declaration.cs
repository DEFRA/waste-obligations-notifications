namespace Defra.WasteObligations.Consumer.Services.WasteObligations;

public sealed record Declaration(
    string? Id,
    int Version,
    int ObligationYear,
    DeclarationOrganisation? Organisation,
    IReadOnlyList<DeclarationAudit>? Audit
);
