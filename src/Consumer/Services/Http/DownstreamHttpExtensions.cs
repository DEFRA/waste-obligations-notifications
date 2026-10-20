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
        bool useAccountProxy = false
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
        builder.ConfigurePrimaryHttpMessageHandler(() => CreateHandler(useAccountProxy));
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

    private static SocketsHttpHandler CreateHandler(bool useAccountProxy)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        if (useAccountProxy)
        {
            var proxy = Environment.GetEnvironmentVariable("HTTP_PROXY");
            handler.UseProxy = proxy is not null;
            if (proxy is not null)
                handler.Proxy = new WebProxy(new UriBuilder(proxy).Uri) { BypassProxyOnLocal = true };
        }

        return handler;
    }
}
