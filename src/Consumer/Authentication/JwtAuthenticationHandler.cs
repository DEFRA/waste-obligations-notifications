using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Authentication;

public sealed class JwtAuthenticationHandler(
    IOptionsMonitor<JwtBearerOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AclOptions> aclOptions
) : JwtBearerHandler(options, logger, encoder)
{
    public const string SchemeName = "Bearer";

    private readonly JwtBearerEvents _events = new()
    {
        OnTokenValidated = context =>
        {
            var identities = context.Principal!.FindAll(Claims.ClientId).ToArray();
            if (
                identities.Length != 1
                || string.IsNullOrWhiteSpace(identities[0].Value)
                || !aclOptions.Value.Clients.TryGetValue(identities[0].Value, out var client)
                || client is not { Type: ClientType.OAuth }
            )
            {
                context.Fail("Failed authorization");

                return Task.CompletedTask;
            }
            // Only configured ACL scopes grant privileges; token scope/role claims cannot elevate access.
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, identities[0].Value),
                new(Claims.ClientId, identities[0].Value),
            };
            claims.AddRange(client.Scopes.Select(scope => new Claim(Claims.Scope, scope)));
            context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));

            return Task.CompletedTask;
        },
    };

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return Task.FromResult(AuthenticateResult.NoResult());
        if (Request.Headers.Authorization.Count == 0)
            return Task.FromResult(AuthenticateResult.NoResult());
        if (
            Request.Headers.Authorization.Count != 1
            || !AuthenticationHeaderValue.TryParse(Request.Headers.Authorization[0], out var header)
        )
            return Task.FromResult(AuthenticateResult.Fail("Failed authorization"));
        if (!string.Equals(header.Scheme, SchemeName, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());
        // The reference handler sets events here because framework initialization replaces constructor events.
        Events = _events;

        return base.HandleAuthenticateAsync();
    }
}
