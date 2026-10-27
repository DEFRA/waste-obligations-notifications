using System.Net;

namespace Defra.WasteObligations.Consumer.Services.Http;

public static class DownstreamHttpHandler
{
    public static HttpMessageHandler Create(bool useProxy, bool renewPooledConnections = true)
    {
        var proxy = useProxy ? Environment.GetEnvironmentVariable("HTTP_PROXY") : null;
        var webProxy = proxy is null ? null : new WebProxy(new UriBuilder(proxy).Uri) { BypassProxyOnLocal = true };

        if (!renewPooledConnections)
            return new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = webProxy is not null,
                Proxy = webProxy,
            };

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // Hosted consumers retain their clients, so recycle connections to refresh DNS.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseProxy = webProxy is not null,
            Proxy = webProxy,
        };

        return handler;
    }
}
