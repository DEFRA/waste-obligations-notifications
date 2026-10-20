using Defra.WasteObligations.Consumer.Services.Http;

namespace Defra.WasteObligations.Consumer.Services.WasteOrganisations;

public sealed class WasteOrganisationsOptions : BasicAuthenticationOptions
{
    public const string SectionName = "WasteOrganisations";

    public bool HasReadAccess => SecureAddress(BaseAddress) && HasValidCredentials;
}
