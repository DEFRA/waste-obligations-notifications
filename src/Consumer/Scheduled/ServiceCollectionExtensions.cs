using Defra.WasteObligations.Consumer.Startup;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.Scheduled;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNotSubmittedReminderProducer(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.TryAddSingleton<ApplicationStartup>();
        services.TryAddSingleton(TimeProvider.System);

        services
            .AddOptions<NotSubmittedReminderOptions>()
            .Bind(configuration.GetSection(NotSubmittedReminderOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.HasValidSchedule, "Not submitted reminder schedule settings are invalid")
            .Validate(
                options => options.ScanLeaseRenewalInterval * 2 <= options.ScanLease,
                "Not submitted reminder lease renewal interval must be no more than half the scan lease"
            )
            .ValidateOnStart();

        services.AddSingleton<NotSubmittedReminderOccurrence>();
        services.AddSingleton<NotSubmittedReminderCutoverGuard>();
        services.AddSingleton<NotSubmittedReminderPresentation>();
        services.AddSingleton<NotSubmittedReminderScanSupport>();
        // Mongo is resolved on first use so the hosted worker does not initialise it at startup.
        services.AddSingleton<IScheduledCampaignRunStore>(serviceProvider => new MongoScheduledCampaignRunStore(
            serviceProvider.GetRequiredService<IMongoDatabase>,
            serviceProvider.GetRequiredService<TimeProvider>()
        ));
        services.AddSingleton<IScheduledCampaignLeaseService>(serviceProvider => new MongoScheduledCampaignLeaseService(
            serviceProvider.GetRequiredService<IMongoDatabase>,
            serviceProvider.GetRequiredService<TimeProvider>()
        ));
        services.AddSingleton<ScheduledCampaignLeaseKeeper>();
        services.AddSingleton<INotSubmittedReminderScanService, NotSubmittedReminderScanService>();
        services.AddSingleton<NotSubmittedReminderScanTrigger>();
        services.AddHostedService<NotSubmittedReminderWorker>();

        return services;
    }
}
