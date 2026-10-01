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
        services
            .AddHttpClient(
                EmfEnvironmentFactory.MetadataClientName,
                client => client.Timeout = Timeout.InfiniteTimeSpan
            )
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddSingleton<EmfDiagnosticLoggerFactory>();
        services.AddSingleton<IEmfEnvironmentFactory, EmfEnvironmentFactory>();
        services.AddHostedService<MetricsExporter>();

        return services;
    }
}
