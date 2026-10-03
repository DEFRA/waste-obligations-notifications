using Amazon.SQS;
using Defra.WasteObligations.Consumer.Startup;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Defra.WasteObligations.Consumer.Consumers;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAnalyticsEventConsumer(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.TryAddSingleton<ApplicationStartup>();

        services
            .AddOptions<AnalyticsEventConsumerOptions>()
            .Bind(configuration.GetRequiredSection(AnalyticsEventConsumerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddAWSService<IAmazonSQS>();
        services.AddHostedService<AnalyticsEventConsumer>();

        return services;
    }
}
