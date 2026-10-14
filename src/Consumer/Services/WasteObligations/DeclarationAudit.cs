namespace Defra.WasteObligations.Consumer.Services.WasteObligations;

public sealed record DeclarationAudit(string? Action, string? Timestamp, DeclarationUser? User);
