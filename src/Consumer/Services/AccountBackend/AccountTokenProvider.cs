using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Services.AccountBackend;

public sealed class AccountTokenProvider(
    IHttpClientFactory factory,
    IOptions<AccountBackendOptions> options,
    TimeProvider clock
) : IDisposable
{
    public const string HttpClientName = "AccountOAuth";
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetToken(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && clock.GetUtcNow() < _expiresAt)
                return _accessToken;

            var settings = options.Value;
            var values = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret,
            };
            if (settings.Scope is not null)
                values.Add("scope", settings.Scope);

            using var client = factory.CreateClient(HttpClientName);
            using var body = new FormUrlEncodedContent(values);
            using var response = await client.PostAsync(settings.TokenEndpoint, body, cancellationToken);
            response.EnsureSuccessStatusCode();
            var token =
                await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Account token response unavailable");
            if (string.IsNullOrWhiteSpace(token.AccessToken) || token.ExpiresIn <= 0)
                throw new InvalidOperationException("Account token response invalid");

            _expiresAt = clock.GetUtcNow().AddSeconds(token.ExpiresIn - 60);
            _accessToken = token.AccessToken;

            return _accessToken;
        }
        finally
        {
            _refresh.Release();
        }
    }

    public void Dispose() => _refresh.Dispose();
}
