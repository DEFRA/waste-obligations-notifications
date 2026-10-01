using Defra.WasteObligations.Consumer.Administration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Defra.WasteObligations.Consumer.Authentication;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAuthenticationAuthorization(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .AddOptions<AclOptions>()
            .Configure(options =>
            {
                if (!configuration.GetValue<bool>($"{CommandDlqAdministrationOptions.SectionName}:Enabled"))
                    return;
                try
                {
                    configuration
                        .GetSection(AclOptions.SectionName)
                        .Bind(options, binding => binding.ErrorOnUnknownConfiguration = true);
                }
                catch (Exception)
                {
                    throw new OptionsValidationException(
                        Options.DefaultName,
                        typeof(AclOptions),
                        ["ACL configuration is invalid."]
                    );
                }
            })
            .ValidateDataAnnotations()
            .Validate(
                options =>
                    !configuration.GetValue<bool>($"{CommandDlqAdministrationOptions.SectionName}:Enabled")
                    || options.HasConfiguredAdmin,
                "Configured ApiKey or OAuth clients and an admin scope are required when command DLQ administration is enabled."
            )
            .ValidateOnStart();
        services
            .AddAuthentication(BasicAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(
                BasicAuthenticationHandler.SchemeName,
                _ => { }
            )
            .AddScheme<JwtBearerOptions, JwtAuthenticationHandler>(
                JwtAuthenticationHandler.SchemeName,
                options =>
                {
                    options.IncludeErrorDetails = false;
                    options.SaveToken = false;
                    // As in Waste Obligations, the private CDP gateway validates signature, issuer and audience.
                    // Direct backend callers therefore belong to the deployment's trusted network boundary.
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        SignatureValidator = (token, _) => new JsonWebToken(token),
                        ValidateAudience = false,
                        ValidateIssuer = false,
                    };
                }
            );
        services
            .AddAuthorizationBuilder()
            .AddPolicy(
                PolicyNames.Admin,
                policy =>
                    policy
                        .AddAuthenticationSchemes(
                            BasicAuthenticationHandler.SchemeName,
                            JwtAuthenticationHandler.SchemeName
                        )
                        .RequireAuthenticatedUser()
                        .RequireClaim(Claims.Scope, Scopes.Admin)
            );

        return services;
    }
}
