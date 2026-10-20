using Defra.WasteObligations.Consumer.Services.Http;

namespace Defra.WasteObligations.Consumer.Services.AccountBackend;

// Client-credentials token requests are safe to retry, unlike general Account POSTs.
public sealed class AccountOAuthOptions : DownstreamOptions
{
    public const string SectionName = "AccountOAuth";
}
