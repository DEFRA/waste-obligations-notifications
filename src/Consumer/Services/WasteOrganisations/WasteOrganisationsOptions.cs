using System.ComponentModel.DataAnnotations;
using Defra.WasteObligations.Consumer.Services.Http;
using Microsoft.Extensions.Http.Resilience;

namespace Defra.WasteObligations.Consumer.Services.WasteOrganisations;

public sealed class WasteOrganisationsOptions : DownstreamOptions
{
    public const string SectionName = "WasteOrganisations";

    public WasteOrganisationsOptions() => Retry.DisableForUnsafeHttpMethods();

    [Required]
    public string ClientId { get; init; } = "set-automatically-when-deployed";

    [Required]
    public string ClientSecret { get; init; } = "set-automatically-when-deployed";

    public bool HasReadAccess =>
        SecureAddress(BaseAddress) && Configured(ClientId) && Configured(ClientSecret) && !ClientId.Contains(':');
}
