using System.Diagnostics.CodeAnalysis;
using Amazon.SQS;
using Defra.WasteObligations.Consumer.Administration;
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
    public static IServiceCollection AddHealth(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<ApplicationStartup>();

        var healthChecks = services
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

        var processingEnabled = configuration.GetValue<bool>(
            $"{NotificationCommandDeliveryOptions.SectionName}:ProcessingEnabled"
        );
        healthChecks.AddCheck<MongoMigrationCompletionHealthCheck>(
            "MongoMigrationCompletion",
            tags: [WebApplicationExtensions.Ready, WebApplicationExtensions.Extended]
        );
        healthChecks.Add(
            new HealthCheckRegistration(
                "NotificationCommandQueue",
                serviceProvider => new SqsHealthCheck(
                    serviceProvider.GetRequiredService<IAmazonSQS>(),
                    serviceProvider.GetRequiredService<IOptions<NotificationCommandDeliveryOptions>>().Value.QueueUrl
                ),
                HealthStatus.Unhealthy,
                tags: [WebApplicationExtensions.Extended],
                timeout: TimeSpan.FromSeconds(10)
            )
        );
        healthChecks.Add(
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

        healthChecks.Add(
            new HealthCheckRegistration(
                "NotificationCommandDeadLetterQueue",
                serviceProvider => new SqsHealthCheck(
                    serviceProvider.GetRequiredService<IAmazonSQS>(),
                    serviceProvider.GetRequiredService<IOptions<CommandDlqAdministrationOptions>>().Value.QueueUrl
                ),
                HealthStatus.Unhealthy,
                tags: [WebApplicationExtensions.Extended],
                timeout: TimeSpan.FromSeconds(10)
            )
        );

        if (processingEnabled)
        {
            healthChecks.Add(
                new HealthCheckRegistration(
                    "Notify",
                    serviceProvider => new NotifyHealthCheck(serviceProvider.GetRequiredService<INotifyEmailClient>()),
                    HealthStatus.Unhealthy,
                    tags: [WebApplicationExtensions.Extended],
                    timeout: TimeSpan.FromSeconds(10)
                )
            );
        }

        return services;
    }
}
