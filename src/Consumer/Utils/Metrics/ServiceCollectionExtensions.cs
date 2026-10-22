using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Defra.WasteObligations.Consumer.Utils.Metrics;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationJourneyMetrics(this IServiceCollection services)
    {
        services.AddMetrics();
        services.TryAddSingleton<INotificationJourneyMetrics, NotificationJourneyMetrics>();

        return services;
    }

    public static IServiceCollection AddNotificationCommandMetrics(this IServiceCollection services)
    {
        services.AddNotificationJourneyMetrics();
        services.AddSingleton<INotificationCommandMetrics, NotificationCommandMetrics>();

        return services;
    }

    public static IServiceCollection AddNotificationCommandEmfExport(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .AddOptions<EmfOptions>()
            .Bind(configuration)
            .ValidateDataAnnotations()
            .Validate(
                options => !options.Enabled || options.HasValidNamespace,
                "AWS_EMF_NAMESPACE must be configured when AWS_EMF_ENABLED is true unless AWS_EMF_ENVIRONMENT is Local"
            )
            .ValidateOnStart();
        return services;
    }
}
