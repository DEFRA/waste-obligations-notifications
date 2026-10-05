using System.Diagnostics.CodeAnalysis;
using Amazon.SQS;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Startup;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Utils.Health;

[ExcludeFromCodeCoverage]
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHealth(this IServiceCollection services)
    {
        services.TryAddSingleton<ApplicationStartup>();

        var healthChecks = services
            .AddHealthChecks()
            .AddCheck<EmailDeliveryCutoverHealthCheck>(
                "EmailDeliveryCutover",
                tags: [WebApplicationExtensions.Extended]
            )
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

        healthChecks
            .AddCheck<MongoMigrationCompletionHealthCheck>(
                "MongoMigrationCompletion",
                tags: [WebApplicationExtensions.Ready, WebApplicationExtensions.Extended]
            )
            .Add(
                new HealthCheckRegistration(
                    "NotificationCommandQueue",
                    serviceProvider => new SqsHealthCheck(
                        serviceProvider.GetRequiredService<IAmazonSQS>(),
                        serviceProvider
                            .GetRequiredService<IOptions<NotificationCommandDeliveryOptions>>()
                            .Value.QueueUrl
                    ),
                    HealthStatus.Unhealthy,
                    tags: [WebApplicationExtensions.Extended],
                    timeout: TimeSpan.FromSeconds(10)
                )
            )
            .Add(
                new HealthCheckRegistration(
                    "NotificationDeliveryRecordStore",
                    serviceProvider => new MongoHealthCheck(
                        serviceProvider.GetRequiredService<MongoDB.Driver.IMongoClient>(),
                        serviceProvider.GetRequiredService<IOptions<MongoDbOptions>>().Value.DatabaseName
                    ),
                    HealthStatus.Unhealthy,
                    tags: [WebApplicationExtensions.Extended],
                    timeout: TimeSpan.FromSeconds(10)
                )
            );

        return services;
    }
}
