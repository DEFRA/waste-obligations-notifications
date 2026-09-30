using Amazon.SQS;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;

namespace Defra.WasteObligations.Consumer.Delivery;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationCommandDelivery(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .AddOptions<NotificationCommandDeliveryOptions>()
            .Bind(configuration.GetRequiredSection(NotificationCommandDeliveryOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.ReceiveTimeoutSeconds > options.WaitTimeSeconds,
                "Notification command receive timeout must exceed the long-poll wait"
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
