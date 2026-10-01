using System.Net;
using Amazon.CloudWatch.EMF.Environment;
using Defra.WasteObligations.Consumer.Utils.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Defra.WasteObligations.Consumer.Tests.Utils.Metrics;

public sealed class EmfEnvironmentFactoryTests
{
    [Theory]
    [InlineData("Local", typeof(LocalEnvironment))]
    [InlineData("Lambda", typeof(LambdaEnvironment))]
    [InlineData("Agent", typeof(DefaultEnvironment))]
    [InlineData("ECS", typeof(ECSEnvironment))]
    [InlineData("EC2", typeof(EC2Environment))]
    public void WhenAnEnvironmentOverrideIsConfigured_ShouldUseTheSdkEnvironmentWithoutMetadataRequests(
        string environment,
        Type expectedType
    )
    {
        var requests = 0;
        using var handler = new MetadataHandler(
            (_, _) =>
            {
                requests++;
                throw new InvalidOperationException("No metadata request should be made for an explicit override");
            }
        );
        using var services = CreateServices(new() { ["AWS_EMF_ENVIRONMENT"] = environment }, handler);

        var resolved = services
            .GetRequiredService<IEmfEnvironmentFactory>()
            .Create(TestContext.Current.CancellationToken);

        Assert.IsType(expectedType, resolved);
        Assert.Equal(0, requests);
    }

    [Fact]
    public void WhenTwoFactoriesHaveDifferentHostConfiguration_ShouldPreserveIndependentSdkServiceAndRoutingConfiguration()
    {
        using var first = CreateServices(
            new()
            {
                ["AWS_EMF_ENVIRONMENT"] = "Agent",
                ["AWS_EMF_SERVICE_NAME"] = "first-service",
                ["AWS_EMF_SERVICE_TYPE"] = "first-type",
                ["AWS_EMF_LOG_GROUP_NAME"] = "first-group",
                ["AWS_EMF_LOG_STREAM_NAME"] = "first-stream",
            }
        );
        using var second = CreateServices(
            new()
            {
                ["AWS_EMF_ENVIRONMENT"] = "Agent",
                ["AWS_EMF_SERVICE_NAME"] = "second-service",
                ["AWS_EMF_SERVICE_TYPE"] = "second-type",
                ["AWS_EMF_LOG_GROUP_NAME"] = "second-group",
                ["AWS_EMF_LOG_STREAM_NAME"] = "second-stream",
            }
        );

        var firstEnvironment = Assert.IsType<DefaultEnvironment>(
            first.GetRequiredService<IEmfEnvironmentFactory>().Create(TestContext.Current.CancellationToken)
        );
        var secondEnvironment = Assert.IsType<DefaultEnvironment>(
            second.GetRequiredService<IEmfEnvironmentFactory>().Create(TestContext.Current.CancellationToken)
        );

        Assert.NotSame(firstEnvironment, secondEnvironment);
        Assert.Equal("first-service", firstEnvironment.Name);
        Assert.Equal("first-type", firstEnvironment.Type);
        Assert.Equal("first-group", firstEnvironment.LogGroupName);
        Assert.Equal("first-stream", firstEnvironment.LogStreamName);
        Assert.Equal("second-service", secondEnvironment.Name);
        Assert.Equal("second-type", secondEnvironment.Type);
        Assert.Equal("second-group", secondEnvironment.LogGroupName);
        Assert.Equal("second-stream", secondEnvironment.LogStreamName);
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

    private static ServiceProvider CreateServices(
        Dictionary<string, string?> values,
        HttpMessageHandler? handler = null
    )
    {
        values["AWS_EMF_NAMESPACE"] = "notifications-test";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddNotificationCommandMetrics();
        services.AddNotificationCommandEmfExport(configuration);
        if (handler is not null)
            services
                .AddHttpClient(EmfEnvironmentFactory.MetadataClientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider();
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
