using Amazon.SQS;
using Defra.WasteObligations.Consumer.Startup;
using Defra.WasteObligations.Consumer.Submission;
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
            .Validate(
                options => options.HasValidProcessingBudget,
                "Analytics receive timeout must exceed long polling and visibility must cover receive, read, publish, delete and headroom"
            )
            .ValidateOnStart();

        services.AddSubmittedEmailProducer(configuration);
        services.AddAWSService<IAmazonSQS>();
        services.AddHostedService<AnalyticsEventConsumer>();

        return services;
    }
}
