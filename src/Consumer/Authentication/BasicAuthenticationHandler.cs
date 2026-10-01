using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Authentication;

public sealed class BasicAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AclOptions> aclOptions
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Basic";
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return NoResult();
        if (Request.Headers.Authorization.Count == 0)
            return NoResult();
        if (
            Request.Headers.Authorization.Count != 1
            || !AuthenticationHeaderValue.TryParse(Request.Headers.Authorization[0], out var header)
            || !string.Equals(header.Scheme, SchemeName, StringComparison.OrdinalIgnoreCase)
        )
            return Fail();

        string credentials;
        try
        {
            credentials = s_utf8.GetString(Convert.FromBase64String(header.Parameter ?? ""));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return Fail();
        }
        var parts = credentials.Split(':', 2);
        var clientId = parts[0];
        var secret = parts.Length > 1 ? parts[1] : "";
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secret))
            return Fail();
        if (
            !aclOptions.Value.Clients.TryGetValue(clientId, out var client)
            || client is not { Type: ClientType.ApiKey }
            || client.Secret != secret
        )
            return Fail();
        var claims = new List<Claim> { new(ClaimTypes.Name, clientId), new(Claims.ClientId, clientId) };
        claims.AddRange(client.Scopes.Select(scope => new Claim(Claims.Scope, scope)));
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static Task<AuthenticateResult> NoResult() => Task.FromResult(AuthenticateResult.NoResult());

    private static Task<AuthenticateResult> Fail() => Task.FromResult(AuthenticateResult.Fail("Failed authorization"));
}
