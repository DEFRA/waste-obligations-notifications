using System.Net;
using Amazon.CloudWatch.EMF.Environment;
using Defra.WasteObligations.Consumer.Utils.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Defra.WasteObligations.Consumer.Tests.Utils.Metrics;

public sealed class EmfResourceFetcherTests
{
    [Fact]
    public void WhenLocalExportIsConfigured_ShouldResolveTheSdkEnvironmentThroughProductionRegistration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AWS_EMF_NAMESPACE"] = "notifications-test",
                    ["AWS_EMF_ENVIRONMENT"] = "Local",
                    ["AWS_EMF_SERVICE_NAME"] = "waste-obligations-notifications",
                }
            )
            .Build();
        using var services = new ServiceCollection()
            .AddLogging()
            .AddNotificationCommandMetrics()
            .AddNotificationCommandEmfExport(configuration)
            .BuildServiceProvider();

        var environment = services.GetRequiredService<Func<CancellationToken, IEnvironment>>()(
            TestContext.Current.CancellationToken
        );

        Assert.IsType<LocalEnvironment>(environment);
        Assert.Equal("waste-obligations-notifications", environment.Name);
    }

    [Fact]
    public void WhenTheSdkFetchesMetadata_ShouldPreserveTheRequestedMethodHeadersAndJsonResponse()
    {
        string? method = null;
        string? metadataToken = null;
        using var handler = new MetadataHandler(
            (request, _) =>
            {
                method = request.Method.Method;
                metadataToken = request.Headers.GetValues("metadata-token").Single();

                return new(HttpStatusCode.OK) { Content = new StringContent("{\"version\":\"sdk-metadata\"}") };
            }
        );
        using var client = new HttpClient(handler);
        var fetcher = new EmfResourceFetcher(client, TestContext.Current.CancellationToken);

        var response = fetcher.FetchJson<Dictionary<string, string>>(
            new("http://metadata.test/document"),
            "PUT",
            new() { ["metadata-token"] = "test-token" }
        );

        Assert.Equal("PUT", method);
        Assert.Equal("test-token", metadataToken);
        Assert.Equal("sdk-metadata", response["version"]);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task WhenMetadataIsCancelledOrTimesOut_ShouldCancelTheRequestAndRejectLateSuccess(
        bool returnLateSuccess,
        bool cancelCaller
    )
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawCancellation = false;
        var returnedLateSuccess = false;
        using var startup = new CancellationTokenSource();
        using var handler = new MetadataHandler(
            (_, token) =>
            {
                requested.TrySetResult();
                token.WaitHandle.WaitOne();
                sawCancellation = token.IsCancellationRequested;
                if (!returnLateSuccess)
                    token.ThrowIfCancellationRequested();
                returnedLateSuccess = true;

                return new(HttpStatusCode.OK) { Content = new StringContent("late metadata") };
            }
        );
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var fetcher = new EmfResourceFetcher(client, startup.Token);
        var fetching = Task.Run(
            () => fetcher.FetchString(new("http://metadata.test/document"), "GET"),
            TestContext.Current.CancellationToken
        );
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        if (cancelCaller)
            await startup.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fetching.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken)
        );

        Assert.True(sawCancellation);
        Assert.Equal(returnLateSuccess, returnedLateSuccess);
    }

    private sealed class MetadataHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            response(request, cancellationToken);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(response(request, cancellationToken));
    }
}
