using Defra.WasteObligations.Consumer.Delivery;

namespace Defra.WasteObligations.Consumer.Utils.Metrics;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationCommandMetrics(this IServiceCollection services)
    {
        services.AddMetrics();
        services.AddSingleton<INotificationCommandMetrics, NotificationCommandMetrics>();

        return services;
    }
}
