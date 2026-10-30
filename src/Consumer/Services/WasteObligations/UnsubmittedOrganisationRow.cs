using System.Text.Json.Serialization;

namespace Defra.WasteObligations.Consumer.Services.WasteObligations;

public sealed record UnsubmittedOrganisationRow(
    [property: JsonPropertyName("organisationId")] Guid OrganisationId,
    [property: JsonPropertyName("obligationYear")] int ObligationYear,
    [property: JsonPropertyName("registrationType")] string RegistrationType,
    [property: JsonPropertyName("businessCountry")] string? BusinessCountry,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("referenceNumber")] string ReferenceNumber
);
