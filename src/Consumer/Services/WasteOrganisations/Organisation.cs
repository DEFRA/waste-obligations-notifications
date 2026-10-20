using System.Text.Json.Serialization;
using Defra.WasteObligations.Consumer.Services.WasteObligations;

namespace Defra.WasteObligations.Consumer.Services.WasteOrganisations;

public sealed record Organisation(
    Guid Id,
    [property: JsonConverter(typeof(OptionalCountryConverter))] string? BusinessCountry,
    string? CompaniesHouseNumber
);
