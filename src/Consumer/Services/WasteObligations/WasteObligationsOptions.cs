using Defra.WasteObligations.Consumer.Services.Http;

namespace Defra.WasteObligations.Consumer.Services.WasteObligations;

public sealed class WasteObligationsOptions : BasicAuthenticationOptions
{
    public const string SectionName = "WasteObligations";

    public bool HasReadAccess =>
        Uri.TryCreate(BaseAddress, UriKind.Absolute, out var uri)
        && (uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback))
        && HasValidCredentials;
}
