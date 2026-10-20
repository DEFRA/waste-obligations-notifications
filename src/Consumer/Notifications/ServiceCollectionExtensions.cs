using Defra.WasteObligations.Consumer.Cancellation;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteOrganisations;
using Defra.WasteObligations.Consumer.Submission;

namespace Defra.WasteObligations.Consumer.Notifications;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationDefinitions(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .AddOptions<NotificationDefinitionsOptions>()
            .Bind(configuration.GetSection(NotificationDefinitionsOptions.SectionName))
            .Validate(
                _ =>
                    !configuration.GetSection("Submission").Exists()
                    && !configuration.GetSection("Cancellation").Exists(),
                "Legacy Submission/Cancellation template settings are not supported; migrate overrides to Notifications:Definitions:<definition>:Templates before startup"
            )
            .Validate(
                options => options.HasValidTemplates,
                "Notification definitions require their template mappings as nonempty hyphenated UUIDs"
            )
            .ValidateOnStart();

        services.AddAccountBackendClient(configuration).AddWasteOrganisationsClient(configuration);
        services.AddAnalyticsNotification<SubmittedEmailProducer>();
        services.AddAnalyticsNotification<CancellationEmailProducer>();

        return services;
    }

    private static void AddAnalyticsNotification<TProducer>(this IServiceCollection services)
        where TProducer : class, IAnalyticsNotificationProducer
    {
        services.AddTransient<TProducer>();
        services.AddTransient<IAnalyticsNotificationProducer>(provider => provider.GetRequiredService<TProducer>());
    }
}
