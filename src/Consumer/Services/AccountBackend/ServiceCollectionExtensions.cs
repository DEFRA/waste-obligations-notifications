using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Services.Http;
using Defra.WasteObligations.Consumer.Utils.Health;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Services.AccountBackend;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAccountBackendClient(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .AddOptions<AccountBackendOptions>()
            .Bind(configuration.GetSection(AccountBackendOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate<IOptions<AnalyticsEventConsumerOptions>>(
                (settings, analytics) => !analytics.Value.ProcessingEnabled || settings.HasReadAccess,
                "AccountBackend read access requires secure addresses and configured credentials when analytics processing is enabled"
            )
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AccountTokenProvider>();
        services
            .AddOptions<AccountOAuthOptions>()
            .Bind(configuration.GetSection(AccountOAuthOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services
            .AddHttpClient(AccountTokenProvider.HttpClientName)
            .ConfigureDownstream<AccountOAuthOptions>(
                AccountTokenProvider.HttpClientName,
                useProxy: true,
                renewPooledConnections: false
            );
        services
            .AddHttpClient<IAccountBackendClient, AccountBackendClient>()
            .ConfigureDownstream<AccountBackendOptions>(AccountBackendOptions.SectionName, useProxy: true);
        services
            .AddHealthChecks()
            .AddCheck<AccountBackendHealthCheck>(
                "AccountBackend",
                tags: [Defra.WasteObligations.Consumer.Utils.Health.WebApplicationExtensions.Extended],
                timeout: TimeSpan.FromSeconds(10)
            );

        return services;
    }
}
