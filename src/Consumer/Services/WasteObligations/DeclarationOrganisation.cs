using System.Text.Json.Serialization;

namespace Defra.WasteObligations.Consumer.Services.WasteObligations;

public sealed record DeclarationOrganisation(
    string? Id,
    [property: JsonConverter(typeof(OptionalCountryConverter))] string? BusinessCountry,
    string? RegistrationType,
    string? Regulator,
    string? RegulatorEmail
);
