using Amazon.SQS;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Startup;
using Defra.WasteObligations.Consumer.Utils.Metrics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Notify.Client;
using Notify.Interfaces;

namespace Defra.WasteObligations.Consumer.Delivery;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationCommandDelivery(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.TryAddSingleton<ApplicationStartup>();

        services
            .AddOptions<NotificationCommandDeliveryOptions>()
            .Bind(configuration.GetRequiredSection(NotificationCommandDeliveryOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.HasFifoQueueUrl,
                "Notification command QueueUrl must be a configured FIFO queue URL"
            )
            .Validate(
                options =>
                    options.DiagnosticNotificationTypes is not null
                    && options.DiagnosticNotificationTypes.Length <= 32
                    && options.DiagnosticNotificationTypes.All(NotificationCommandDeliveryOptions.IsDiagnosticLabel),
                "DiagnosticNotificationTypes must contain only bounded lowercase ASCII category labels"
            )
            .Validate(
                options => options.HasValidProcessingBudget,
                "CommandLeaseSeconds and VisibilityTimeoutSeconds must cover ReceiveTimeoutSeconds, ClaimTimeoutSeconds, NotifyTimeoutSeconds, AcceptanceTimeoutSeconds, DeleteTimeoutSeconds and SafetyHeadroomSeconds"
            )
            .Validate(
                options => options.ReceiveTimeoutSeconds > options.WaitTimeSeconds,
                "Notification command receive timeout must exceed the long-poll wait"
            )
            .Validate(
                options => NotificationCommandDeliveryOptions.IsSecretConfigured(options.EvidenceDigestSecret),
                "EvidenceDigestSecret must be configured"
            )
            .Validate(
                options => NotificationCommandDeliveryOptions.IsSecretConfigured(options.RecipientLaneSecret),
                "RecipientLaneSecret must be configured"
            )
            .Validate(
                options => options.TryReadCutover(out _),
                "EmailDeliveryCutoverUtc must be null or include an explicit UTC offset"
            )
            .ValidateOnStart();

        services
            .AddOptions<NotifyOptions>()
            .Bind(configuration.GetSection(NotifyOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.HasValidApiKey, "Notify ApiKey must be configured")
            .Validate(
                options =>
                    Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var uri)
                    && uri.Scheme is "https" or "http",
                "Notify BaseAddress must be an absolute HTTP URL"
            )
            .ValidateOnStart();
        services.AddSingleton<Func<IHttpClient, NotifyOptions, IAsyncNotificationClient>>(_ =>
            (transport, notify) => new NotificationClient(transport, notify.ApiKey)
        );
        services
            .AddHttpClient<INotifyEmailClient, NotifyEmailClient>(
                (provider, client) =>
                {
                    client.BaseAddress = new Uri(
                        provider
                            .GetRequiredService<Microsoft.Extensions.Options.IOptions<NotifyOptions>>()
                            .Value.BaseAddress
                    );
                    client.Timeout = Timeout.InfiniteTimeSpan;
                }
            )
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();

        services.AddAWSService<IAmazonSQS>();
        services.AddMongo(configuration);
        services.AddMongoMigrations(configuration);
        services.AddSingleton<INotificationCommandDigest, NotificationCommandDigest>();
        services.AddSingleton<INotificationDeliveryRecordStore, MongoNotificationDeliveryRecordStore>();
        services.AddSingleton<INotificationDeliveryRecordStoreFactory, NotificationDeliveryRecordStoreFactory>();
        services.AddNotificationCommandMetrics();
        services.AddNotificationCommandEmfExport(configuration);
        services.AddSingleton<INotificationCommandPublisher, NotificationCommandPublisher>();
        services.AddHostedService<NotificationCommandConsumer>();

        return services;
    }
}
