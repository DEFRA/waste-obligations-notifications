using System.Text.Json.Serialization;

namespace Defra.WasteObligations.Consumer.Services.WasteObligations;

public sealed record UnsubmittedOrganisationsPage(
    [property: JsonPropertyName("unsubmittedOrganisations")]
        IReadOnlyList<UnsubmittedOrganisationRow> UnsubmittedOrganisations,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize
);
