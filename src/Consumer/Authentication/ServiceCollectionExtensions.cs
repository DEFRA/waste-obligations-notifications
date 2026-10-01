using Defra.WasteObligations.Consumer.Administration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

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
                "Configured ApiKey clients and an admin scope are required when command DLQ administration is enabled."
            )
            .ValidateOnStart();
        services
            .AddAuthentication(BasicAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(
                BasicAuthenticationHandler.SchemeName,
                _ => { }
            );
        services
            .AddAuthorizationBuilder()
            .AddPolicy(
                PolicyNames.Admin,
                policy =>
                    policy
                        .AddAuthenticationSchemes(BasicAuthenticationHandler.SchemeName)
                        .RequireAuthenticatedUser()
                        .RequireClaim(Claims.Scope, Scopes.Admin)
            );

        return services;
    }
}
