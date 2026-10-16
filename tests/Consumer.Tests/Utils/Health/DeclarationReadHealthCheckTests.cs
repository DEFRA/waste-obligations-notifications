using System.Net;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
using Defra.WasteObligations.Consumer.Utils.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Tests.Utils.Health;

public sealed class DeclarationReadHealthCheckTests
{
    [Fact]
    public async Task WhenAnalyticsIsDisabled_ShouldBeHealthyWithoutReadingService()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Read should not occur"));
        using var client = CreateClient(handler);
        var check = CreateCheck(client, false);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("Analytics processing disabled", result.Description);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task WhenReadServiceIsAvailable_ShouldBeHealthyAndAuthenticateHealthRequest()
    {
        using var handler = new Handler(
            (request, token) =>
            {
                Assert.Equal("/api/health/authorized", request.RequestUri!.AbsolutePath);
                Assert.Equal("Basic dGVzdC1jbGllbnQ6dGVzdC1zZWNyZXQ=", request.Headers.Authorization!.ToString());
                Assert.True(token.CanBeCanceled);

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        );
        using var client = CreateClient(handler);
        var check = CreateCheck(client, true);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenReadServiceFails_ShouldExposeOnlyFixedFailure(bool throws)
    {
        const string privateResponse = "private read credential and recipient@example.test";
        using var handler = new Handler(
            (_, _) =>
                throws
                    ? throw new HttpRequestException(privateResponse)
                    : Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.Unauthorized)
                        {
                            Content = new StringContent(privateResponse),
                        }
                    )
        );
        using var client = CreateClient(handler);
        var check = CreateCheck(client, true);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Declaration read service unavailable", result.Description);
        Assert.Null(result.Exception);
        Assert.Empty(result.Data);
        Assert.Equal(1, handler.RequestCount);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://declarations.test/api/"), Timeout = Timeout.InfiniteTimeSpan };

    private static DeclarationReadHealthCheck CreateCheck(HttpClient client, bool enabled) =>
        new(
            Options.Create(
                new AnalyticsEventConsumerOptions
                {
                    ProcessingEnabled = enabled,
                    QueueUrl = "http://sqs.test/analytics",
                }
            ),
            new WasteObligationsClient(
                client,
                Options.Create(new WasteObligationsOptions { Username = "test-client", Password = "test-secret" })
            )
        );

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestCount++;

            return send(request, cancellationToken);
        }
    }
}
