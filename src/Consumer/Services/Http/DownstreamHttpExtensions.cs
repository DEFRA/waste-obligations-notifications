using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;

namespace Defra.WasteObligations.Consumer.Services.Http;

public static class DownstreamHttpExtensions
{
    public static IHttpClientBuilder ConfigureDownstream<TOptions>(
        this IHttpClientBuilder builder,
        string name,
        bool useProxy,
        bool renewPooledConnections = true
    )
        where TOptions : DownstreamOptions
    {
        builder.ConfigureHttpClient(
            (provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<TOptions>>().Value;
                if (DownstreamOptions.SecureAddress(options.BaseAddress))
                {
                    var address = new UriBuilder(options.BaseAddress);
                    if (!address.Path.EndsWith('/'))
                        address.Path += "/";

                    client.BaseAddress = address.Uri;
                }

                if (client.BaseAddress?.Scheme == "https")
                    client.DefaultRequestVersion = HttpVersion.Version20;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                client.Timeout = Timeout.InfiniteTimeSpan;
            }
        );
        builder.ConfigurePrimaryHttpMessageHandler(() =>
            DownstreamHttpHandler.Create(useProxy, renewPooledConnections)
        );
        builder.RemoveAllLoggers();
        builder.AddResilienceHandler(
            name,
            (pipeline, context) =>
            {
                var options = context.ServiceProvider.GetRequiredService<IOptions<TOptions>>().Value;
                pipeline.ConfigureTelemetry(NullLoggerFactory.Instance);
                pipeline
                    .AddTimeout(options.TotalRequestTimeout)
                    .AddRetry(options.Retry)
                    .AddTimeout(options.AttemptTimeout);
            }
        );

        return builder;
    }
}
