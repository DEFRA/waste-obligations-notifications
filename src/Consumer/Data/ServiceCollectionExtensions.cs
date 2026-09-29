using Defra.WasteObligations.Consumer.Delivery;

namespace Defra.WasteObligations.Consumer.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMongoMigrations(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<MongoMigrationOptions>()
            .Bind(configuration.GetSection(MongoMigrationOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.LeaseRenewalIntervalSeconds * 2 <= options.LeaseDurationSeconds,
                "Mongo migration lease renewal interval must be no more than half the lease duration"
            )
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<MongoMigrationReadiness>();
        services.AddSingleton<IMongoMigrationLeaseService, MongoMigrationLeaseService>();
        services.AddSingleton<IMongoMigrationRunner, MongoMigrationRunner>();
        if (configuration.GetValue<bool>($"{NotificationCommandDeliveryOptions.SectionName}:ProcessingEnabled"))
        {
            services.AddHostedService<MongoMigrationService>();
        }

        return services;
    }
}
