using System.Net;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
using Defra.WasteObligations.Consumer.Services.WasteOrganisations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Defra.WasteObligations.Consumer.Tests.Services;

[CollectionDefinition(nameof(DownstreamProxyTests), DisableParallelization = true)]
public sealed class DownstreamProxyCollection;

[Collection(nameof(DownstreamProxyTests))]
public sealed class DownstreamProxyTests
{
    [Fact]
    public void WhenProxyIsConfigured_ShouldUseItOnlyForSelectedIntegrations()
    {
        var original = Environment.GetEnvironmentVariable("HTTP_PROXY");
        try
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", "http://127.0.0.1:18888");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
                        ["NotificationCommandDelivery:QueueUrl"] = "http://localhost:4566/commands.fifo",
                    }
                )
                .Build();
            var services = new ServiceCollection();
            services.AddNotificationCommandDelivery(configuration);
            services.AddAccountBackendClient(configuration);
            services.AddWasteObligationsClient(configuration);
            services.AddWasteOrganisationsClient(configuration);
            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

            foreach (var name in new[] { nameof(INotifyEmailClient), nameof(IAccountBackendClient) })
            {
                var handler = GetSocketsHandler(factory, name);
                Assert.True(handler.UseProxy);
                var proxy = Assert.IsType<WebProxy>(handler.Proxy);
                Assert.Equal(new Uri("http://127.0.0.1:18888"), proxy.Address);
                Assert.True(proxy.BypassProxyOnLocal);
            }

            var oauth = Assert.IsType<HttpClientHandler>(
                GetPrimaryHandler(factory, AccountTokenProvider.HttpClientName)
            );
            Assert.True(oauth.UseProxy);
            var oauthProxy = Assert.IsType<WebProxy>(oauth.Proxy);
            Assert.Equal(new Uri("http://127.0.0.1:18888"), oauthProxy.Address);
            Assert.True(oauthProxy.BypassProxyOnLocal);

            Assert.False(GetSocketsHandler(factory, nameof(IWasteObligationsClient)).UseProxy);
            Assert.False(GetSocketsHandler(factory, nameof(IWasteOrganisationsClient)).UseProxy);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", original);
        }
    }

    [Fact]
    public void WhenProxyIsAbsent_ShouldDisableItForNotify()
    {
        var original = Environment.GetEnvironmentVariable("HTTP_PROXY");
        var originalHttps = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        try
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", null);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://127.0.0.1:18888");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
                        ["NotificationCommandDelivery:QueueUrl"] = "http://localhost:4566/commands.fifo",
                    }
                )
                .Build();
            var services = new ServiceCollection();
            services.AddNotificationCommandDelivery(configuration);
            using var provider = services.BuildServiceProvider();

            Assert.False(
                GetSocketsHandler(
                    provider.GetRequiredService<IHttpMessageHandlerFactory>(),
                    nameof(INotifyEmailClient)
                ).UseProxy
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", original);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", originalHttps);
        }
    }

    private static SocketsHttpHandler GetSocketsHandler(IHttpMessageHandlerFactory factory, string name) =>
        Assert.IsType<SocketsHttpHandler>(GetPrimaryHandler(factory, name));

    private static HttpMessageHandler GetPrimaryHandler(IHttpMessageHandlerFactory factory, string name)
    {
        var handler = factory.CreateHandler(name);
        while (handler is DelegatingHandler delegatingHandler)
            handler = delegatingHandler.InnerHandler!;

        return handler;
    }
}
