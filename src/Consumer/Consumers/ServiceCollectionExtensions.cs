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
                options => options.ReceiveTimeoutSeconds > options.WaitTimeSeconds,
                "Analytics receive timeout must exceed the long-poll wait"
            )
            .Validate(
                options => options.HasValidProcessingBudget,
                "Analytics visibility must cover the processing budget"
            )
            .ValidateOnStart();

        services.AddAWSService<IAmazonSQS>();
        services.AddSingleton<AnalyticsProcessingBudget>();
        services.AddSubmissionEmails(configuration);
        services.AddHostedService<AnalyticsEventConsumer>();

        return services;
    }
}
