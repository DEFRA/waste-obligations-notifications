using Amazon.SQS;

namespace Defra.WasteObligations.Consumer.Consumers;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAnalyticsEventConsumer(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
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
