using Amazon.CloudWatch.EMF.Environment;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Environments = Amazon.CloudWatch.EMF.Environment.Environments;

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
            .AddHttpClient(nameof(EmfResourceFetcher), client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddSingleton<Func<CancellationToken, IEnvironment>>(
            (IServiceProvider provider) =>
                (CancellationToken cancellationToken) =>
                {
                    var settings = provider.GetRequiredService<IOptions<EmfOptions>>().Value;
                    var configuration = new Amazon.CloudWatch.EMF.Config.Configuration(
                        settings.ServiceName ?? Metrics.ServiceName,
                        settings.ServiceType,
                        settings.LogGroupName,
                        settings.LogStreamName,
                        settings.AgentEndpoint,
                        settings.AgentBufferSize,
                        Enum.TryParse<Environments>(settings.Environment, out var environment)
                            ? environment
                            : Environments.Unknown
                    );
                    using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    startup.CancelAfter(TimeSpan.FromSeconds(8));
                    using var client = provider
                        .GetRequiredService<IHttpClientFactory>()
                        .CreateClient(nameof(EmfResourceFetcher));
                    var resolved = new EnvironmentProvider(
                        configuration,
                        new EmfResourceFetcher(client, startup.Token),
                        NullLoggerFactory.Instance
                    ).ResolveEnvironment();
                    startup.Token.ThrowIfCancellationRequested();

                    return resolved;
                }
        );
        services.AddHostedService<MetricsExporter>();

        return services;
    }
}
