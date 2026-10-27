using Defra.WasteObligations.Consumer.Startup;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
            .ValidateOnStart();

        services.AddSingleton<NotSubmittedReminderOccurrence>();
        services.AddSingleton<NotSubmittedReminderCutoverGuard>();
        services.AddSingleton<NotSubmittedReminderPresentation>();
        services.AddSingleton<NotSubmittedReminderScanSupport>();
        services.AddSingleton<INotSubmittedReminderScanService, NotSubmittedReminderScanService>();
        services.AddSingleton<NotSubmittedReminderScanTrigger>();
        services.AddHostedService<NotSubmittedReminderWorker>();

        return services;
    }
}
