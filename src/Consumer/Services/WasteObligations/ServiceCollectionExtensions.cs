using Defra.WasteObligations.Consumer.Consumers;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;

namespace Defra.WasteObligations.Consumer.Services.WasteObligations;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWasteObligationsClient(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .AddOptions<WasteObligationsOptions>()
            .Bind(configuration.GetSection(WasteObligationsOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate<IOptions<AnalyticsEventConsumerOptions>>(
                (options, analytics) => !analytics.Value.ProcessingEnabled || options.HasReadAccess,
                "Waste Obligations read access requires HTTPS or loopback HTTP and configured Basic credentials when analytics processing is enabled"
            )
            .ValidateOnStart();

        services
            .AddHttpClient<IWasteObligationsClient, WasteObligationsClient>(
                (provider, client) =>
                {
                    var options = provider.GetRequiredService<IOptions<WasteObligationsOptions>>().Value;
                    if (options.HasReadAccess)
                    {
                        var address = new UriBuilder(options.BaseAddress);
                        if (!address.Path.EndsWith('/'))
                            address.Path += "/";

                        client.BaseAddress = address.Uri;
                    }

                    client.Timeout = Timeout.InfiniteTimeSpan;
                }
            )
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) }
            )
            // Default HTTP failure logs attach raw exception messages, which can contain private data.
            // Keep the consumer's fixed failure reasons and safe correlation instead.
            .RemoveAllLoggers()
            .AddResilienceHandler(
                WasteObligationsOptions.SectionName,
                (builder, context) =>
                {
                    var options = context.ServiceProvider.GetRequiredService<IOptions<WasteObligationsOptions>>().Value;

                    // Exception text can contain private downstream data. Scope suppression to this pipeline.
                    builder.ConfigureTelemetry(NullLoggerFactory.Instance);
                    builder
                        .AddTimeout(options.TotalRequestTimeout)
                        .AddRetry(options.Retry)
                        .AddTimeout(options.AttemptTimeout);
                }
            );

        return services;
    }
}
