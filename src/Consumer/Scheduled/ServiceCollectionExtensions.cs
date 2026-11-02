using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Notifications;
using Defra.WasteObligations.Consumer.Startup;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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

        foreach (var campaign in NotSubmittedReminderCampaign.All)
            services.AddNotSubmittedReminderCampaign(campaign, configuration);

        return services;
    }

    private static void AddNotSubmittedReminderCampaign(
        this IServiceCollection services,
        NotSubmittedReminderCampaign campaign,
        IConfiguration configuration
    )
    {
        services
            .AddOptions<NotSubmittedReminderOptions>(campaign.SectionName)
            .Bind(configuration.GetSection(campaign.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.HasValidSchedule, $"{campaign.SectionName} schedule settings are invalid")
            .Validate(
                options => options.ScanLeaseRenewalInterval * 2 <= options.ScanLease,
                $"{campaign.SectionName} lease renewal interval must be no more than half the scan lease"
            )
            .Validate(
                options => !options.Enabled || campaign.HasValidPersonalisation(options.Personalisation),
                $"{campaign.SectionName} personalisation must set every required key, use non-blank values and not replace computed keys"
            )
            .ValidateOnStart();

        var key = campaign.Id;
        services.AddKeyedSingleton<IOptions<NotSubmittedReminderOptions>>(
            key,
            (serviceProvider, _) =>
                Options.Create(
                    serviceProvider
                        .GetRequiredService<IOptionsMonitor<NotSubmittedReminderOptions>>()
                        .Get(campaign.SectionName)
                )
        );
        services.AddKeyedSingleton<INotSubmittedReminderScanService>(
            key,
            (serviceProvider, _) =>
                ActivatorUtilities.CreateInstance<NotSubmittedReminderScanService>(
                    serviceProvider,
                    CreateScanSupport(serviceProvider, campaign)
                )
        );
        services.AddKeyedSingleton(
            key,
            (serviceProvider, _) =>
                new NotSubmittedReminderScanTrigger(
                    serviceProvider.GetRequiredKeyedService<INotSubmittedReminderScanService>(key)
                )
        );
        // Added directly because AddHostedService keeps only one registration per worker type.
        services.Add(
            ServiceDescriptor.Singleton<IHostedService, NotSubmittedReminderWorker>(serviceProvider =>
                ActivatorUtilities.CreateInstance<NotSubmittedReminderWorker>(
                    serviceProvider,
                    serviceProvider.GetRequiredKeyedService<IOptions<NotSubmittedReminderOptions>>(key),
                    serviceProvider.GetRequiredKeyedService<INotSubmittedReminderScanService>(key),
                    serviceProvider.GetRequiredKeyedService<NotSubmittedReminderScanTrigger>(key)
                )
            )
        );
    }

    private static NotSubmittedReminderScanSupport CreateScanSupport(
        IServiceProvider serviceProvider,
        NotSubmittedReminderCampaign campaign
    )
    {
        var options = serviceProvider.GetRequiredKeyedService<IOptions<NotSubmittedReminderOptions>>(campaign.Id);
        var timeProvider = serviceProvider.GetRequiredService<TimeProvider>();
        var occurrence = new NotSubmittedReminderOccurrence(options);
        var presentation = new NotSubmittedReminderPresentation(
            campaign,
            options,
            serviceProvider.GetRequiredService<IOptions<NotificationDefinitionsOptions>>()
        );
        var cutoverGuard = new NotSubmittedReminderCutoverGuard(
            options,
            serviceProvider.GetRequiredService<IOptions<NotificationCommandDeliveryOptions>>(),
            occurrence,
            timeProvider
        );

        return new NotSubmittedReminderScanSupport(
            campaign,
            options,
            presentation,
            occurrence,
            cutoverGuard,
            timeProvider
        );
    }
}
