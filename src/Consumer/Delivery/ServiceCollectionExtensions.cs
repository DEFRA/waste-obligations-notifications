using System.ComponentModel.DataAnnotations;
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
        var processingEnabled = configuration.GetValue<bool>(
            $"{NotificationCommandDeliveryOptions.SectionName}:ProcessingEnabled"
        );
        services.TryAddSingleton<ApplicationStartup>();

        services
            .AddOptions<NotificationCommandDeliveryOptions>()
            .Bind(configuration.GetRequiredSection(NotificationCommandDeliveryOptions.SectionName))
            .Validate(
                options =>
                    !options.ProcessingEnabled
                    || Validator.TryValidateObject(options, new ValidationContext(options), [], true),
                "Notification command sending configuration must satisfy required fields and duration ranges."
            )
            .Validate(
                options =>
                    options.DiagnosticNotificationTypes is not null
                    && options.DiagnosticNotificationTypes.Length <= 32
                    && options.DiagnosticNotificationTypes.All(NotificationCommandDeliveryOptions.IsDiagnosticLabel),
                "DiagnosticNotificationTypes must contain only bounded lowercase ASCII category labels"
            )
            .Validate(
                options => !options.ProcessingEnabled || options.HasValidProcessingBudget,
                "CommandLeaseSeconds and VisibilityTimeoutSeconds must cover ReceiveTimeoutSeconds, ClaimTimeoutSeconds, NotifyTimeoutSeconds, AcceptanceTimeoutSeconds, DeleteTimeoutSeconds and SafetyHeadroomSeconds"
            )
            .Validate(
                options => !options.ProcessingEnabled || options.ReceiveTimeoutSeconds > options.WaitTimeSeconds,
                "Notification command receive timeout must exceed the long-poll wait"
            )
            .Validate(
                options => NotificationCommandDeliveryOptions.IsSecretConfigured(options.EvidenceDigestSecret),
                "EvidenceDigestSecret must be configured for notification command administration"
            )
            .Validate(
                options => NotificationCommandDeliveryOptions.IsSecretConfigured(options.RecipientLaneSecret),
                "RecipientLaneSecret must be configured for notification command administration"
            )
            .Validate(
                options => !options.ProcessingEnabled || options.TryReadCutover(out _),
                "EmailDeliveryCutoverUtc must be null or include an explicit UTC offset when notification command processing is enabled"
            )
            .ValidateOnStart();

        services.AddNotifySending(configuration, processingEnabled);

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

    private static void AddNotifySending(
        this IServiceCollection services,
        IConfiguration configuration,
        bool processingEnabled
    )
    {
        services
            .AddOptions<NotifyOptions>()
            .Bind(configuration.GetSection(NotifyOptions.SectionName))
            .Validate(
                options =>
                    !processingEnabled
                    || Validator.TryValidateObject(options, new ValidationContext(options), [], true),
                "Notify configuration must satisfy required sending fields."
            )
            .Validate(
                options => !processingEnabled || options.HasValidApiKey,
                "Notify ApiKey must be configured when notification command processing is enabled"
            )
            .Validate(
                options =>
                    !processingEnabled
                    || (
                        Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var uri)
                        && uri.Scheme is "https" or "http"
                    ),
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
                    if (processingEnabled)
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
    }
}
