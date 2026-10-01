using Defra.WasteObligations.Consumer.Administration;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using MongoDB.Driver.Authentication.AWS;

namespace Defra.WasteObligations.Consumer.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMongo(this IServiceCollection services, IConfiguration configuration)
    {
        RegisterConventions();

        services
            .AddOptions<MongoDbOptions>()
            .Bind(configuration.GetRequiredSection(MongoDbOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IMongoClient>(serviceProvider =>
        {
            MongoClientSettings.Extensions.AddAWSAuthentication();

            var options = serviceProvider.GetRequiredService<IOptions<MongoDbOptions>>().Value;
            var settings = MongoClientSettings.FromConnectionString(options.DatabaseUri);
            settings.ApplicationName = "waste-obligations-notifications-consumer";
            // Duplicate-key recovery reads the existing delivery record to distinguish a duplicate from a conflict.
            // Read from the primary so replication lag cannot hide the record that caused the insert to fail.
            settings.ReadPreference = ReadPreference.Primary;

            return new MongoClient(settings);
        });
        services.AddSingleton(serviceProvider =>
            serviceProvider
                .GetRequiredService<IMongoClient>()
                .GetDatabase(serviceProvider.GetRequiredService<IOptions<MongoDbOptions>>().Value.DatabaseName)
        );

        return services;
    }

    public static void RegisterConventions()
    {
        var conventionPack = new ConventionPack
        {
            new CamelCaseElementNameConvention(),
            new EnumRepresentationConvention(BsonType.String),
        };

        ConventionRegistry.Register(nameof(conventionPack), conventionPack, _ => true);
    }

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
        if (
            configuration.GetValue<bool>($"{NotificationCommandDeliveryOptions.SectionName}:ProcessingEnabled")
            || configuration.GetValue<bool>($"{CommandDlqAdministrationOptions.SectionName}:Enabled")
        )
        {
            services.AddHostedService<MongoMigrationService>();
        }

        return services;
    }
}
