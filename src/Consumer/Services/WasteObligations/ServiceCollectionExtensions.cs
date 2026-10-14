using Defra.WasteObligations.Consumer.Consumers;
using Microsoft.Extensions.Http.Resilience;
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
        // Polly telemetry formats exception results; preserve only the consumer's fixed diagnostics.
        services.AddLogging(logging => logging.AddFilter("Polly", LogLevel.None));

        services
            .AddOptions<WasteObligationsOptions>()
            .Bind(configuration.GetSection(WasteObligationsOptions.SectionName))
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
                        client.DefaultRequestVersion =
                            address.Scheme == Uri.UriSchemeHttps ? HttpVersion.Version20 : HttpVersion.Version11;
                    }

                    client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                    client.Timeout = Timeout.InfiniteTimeSpan;
                }
            )
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) }
            )
            .RemoveAllLoggers()
            .AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(3);
                options.Retry.MaxRetryAttempts = 2;
                options.Retry.Delay = TimeSpan.FromMilliseconds(200);
                options.Retry.BackoffType = DelayBackoffType.Exponential;
                options.Retry.UseJitter = true;
                options.Retry.DisableForUnsafeHttpMethods();
            });

        return services;
    }
}
using System.Net;
