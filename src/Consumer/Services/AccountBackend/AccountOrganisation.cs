namespace Defra.WasteObligations.Consumer.Services.AccountBackend;

public sealed record AccountOrganisation(string? ExternalId, string? CompaniesHouseNumber, bool IsComplianceScheme);
