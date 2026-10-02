using Amazon.SQS;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Startup;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
                options =>
                    options.DiagnosticNotificationTypes is not null
                    && options.DiagnosticNotificationTypes.All(NotificationCommandDeliveryOptions.IsDiagnosticLabel),
                "DiagnosticNotificationTypes must contain only bounded lowercase ASCII category labels"
            )
            .Validate(
                options => options.ReceiveTimeoutSeconds > options.WaitTimeSeconds,
                "Notification command receive timeout must exceed the long-poll wait"
            )
            .Validate(
                options =>
                    !options.ProcessingEnabled
                    || NotificationCommandDeliveryOptions.IsSecretConfigured(options.EvidenceDigestSecret),
                "EvidenceDigestSecret must be configured when notification command processing is enabled"
            )
            .Validate(
                options =>
                    !options.ProcessingEnabled
                    || NotificationCommandDeliveryOptions.IsSecretConfigured(options.RecipientLaneSecret),
                "RecipientLaneSecret must be configured when notification command processing is enabled"
            )
            .Validate(
                options => !options.ProcessingEnabled || options.TryReadCutover(out _),
                "EmailDeliveryCutoverUtc must be null or include an explicit UTC offset when notification command processing is enabled"
            )
            .ValidateOnStart();

        services.AddAWSService<IAmazonSQS>();
        services.AddMongo(configuration);
        services.AddMongoMigrations(configuration);
        services.AddSingleton<INotificationCommandDigest, NotificationCommandDigest>();
        services.AddSingleton<INotificationDeliveryRecordStore, MongoNotificationDeliveryRecordStore>();
        services.AddSingleton<INotificationDeliveryRecordStoreFactory, NotificationDeliveryRecordStoreFactory>();
        services.AddSingleton<NotificationCommandMetrics>();
        services.AddSingleton<INotificationCommandPublisher, NotificationCommandPublisher>();
        services.AddHostedService<NotificationCommandConsumer>();

        return services;
    }
}
