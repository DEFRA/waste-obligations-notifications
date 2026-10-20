using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Services.Http;
using Defra.WasteObligations.Consumer.Utils.Health;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Services.WasteOrganisations;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWasteOrganisationsClient(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .AddOptions<WasteOrganisationsOptions>()
            .Bind(configuration.GetSection(WasteOrganisationsOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate<IOptions<AnalyticsEventConsumerOptions>>(
                (settings, analytics) => !analytics.Value.ProcessingEnabled || settings.HasReadAccess,
                "WasteOrganisations read access requires secure addresses and configured credentials when analytics processing is enabled"
            )
            .ValidateOnStart();
        services
            .AddHttpClient<IWasteOrganisationsClient, WasteOrganisationsClient>()
            .ConfigureDownstream<WasteOrganisationsOptions>(WasteOrganisationsOptions.SectionName);
        services
            .AddHealthChecks()
            .AddCheck<WasteOrganisationsHealthCheck>(
                "WasteOrganisations",
                tags: [Defra.WasteObligations.Consumer.Utils.Health.WebApplicationExtensions.Extended],
                timeout: TimeSpan.FromSeconds(10)
            );

        return services;
    }
}
