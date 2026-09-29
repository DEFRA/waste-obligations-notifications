using System.Diagnostics.CodeAnalysis;
using Amazon.SQS;
using Defra.WasteObligations.Consumer.Consumers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Utils.Health;

[ExcludeFromCodeCoverage]
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHealth(this IServiceCollection services)
    {
        services
            .AddHealthChecks()
            .Add(
                new HealthCheckRegistration(
                    "AnalyticsEventQueue",
                    serviceProvider => new SqsHealthCheck(
                        serviceProvider.GetRequiredService<IAmazonSQS>(),
                        serviceProvider.GetRequiredService<IOptions<AnalyticsEventConsumerOptions>>().Value.QueueUrl
                    ),
                    HealthStatus.Unhealthy,
                    tags: [WebApplicationExtensions.Extended],
                    timeout: TimeSpan.FromSeconds(10)
                )
            );

        return services;
    }
}
