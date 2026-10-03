using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests;

public sealed class NotifyHealthTests
{
    private const string PrivateData =
        "recipient@example.com private-personalisation private-template private-notify-id";

    [Theory]
    [InlineData(null)]
    [InlineData("2100-01-01T00:00:00Z")]
    public async Task WhenCutoverIsNullOrConfigured_ShouldAlwaysCheckNotifyOnlyOnExtendedHealth(string? cutover)
    {
        using var handler = new ControlledHandler(
            (_, _) =>
                Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"templates\":[{\"id\":\"private-template\",\"type\":\"email\",\"body\":\""
                                + PrivateData
                                + "\"}]}"
                        ),
                    }
                )
        );
        await using var factory = new HealthApplicationFactory(cutover, handler);
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(0, handler.RequestCount);

        using var extended = await client.GetAsync("/health/all", TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(
            await extended.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );

        Assert.Equal(HttpStatusCode.OK, extended.StatusCode);
        var notify = body.RootElement.GetProperty("results").GetProperty("Notify");
        Assert.Equal("Healthy", notify.GetProperty("status").GetString());
        Assert.Equal("Connected to GOV.UK Notify.", notify.GetProperty("description").GetString());
        var cutoverData = body
            .RootElement.GetProperty("results")
            .GetProperty("EmailDeliveryCutover")
            .GetProperty("data");
        Assert.True(cutoverData.GetProperty("cutoverValid").GetBoolean());
        Assert.Equal(cutover is null ? "suppress-all" : "boundary", cutoverData.GetProperty("mode").GetString());
        if (cutover is null)
            Assert.Equal(JsonValueKind.Null, cutoverData.GetProperty("emailDeliveryCutoverUtc").ValueKind);
        Assert.Equal(1, handler.RequestCount);
        var text = body.RootElement.GetRawText();
        foreach (var value in PrivateData.Split(' ').Append(NotifyTestCredentials.ApiKey))
        {
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
            Assert.All(
                factory.Logs.Messages,
                message => Assert.DoesNotContain(value, message, StringComparison.Ordinal)
            );
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenNotifyFails_ShouldExposeOnlyFixedFailureAndKeepReadinessIndependent(bool throws)
    {
        using var handler = new ControlledHandler(
            (_, _) =>
                throws
                    ? throw new HttpRequestException(PrivateData)
                    : Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(PrivateData) }
                    )
        );
        await using var factory = new HealthApplicationFactory("2100-01-01T00:00:00Z", handler);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/all", TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(text);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var notify = body.RootElement.GetProperty("results").GetProperty("Notify");
        Assert.Equal("Unhealthy", notify.GetProperty("status").GetString());
        Assert.Equal("Failed to connect to GOV.UK Notify.", notify.GetProperty("description").GetString());
        Assert.False(notify.TryGetProperty("exception", out _));
        Assert.False(notify.TryGetProperty("data", out _));
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains(
            factory.Logs.Messages,
            message =>
                message.Contains("Notify", StringComparison.Ordinal)
                && message.Contains("Unhealthy", StringComparison.Ordinal)
        );
        foreach (var value in PrivateData.Split(' ').Append(NotifyTestCredentials.ApiKey))
        {
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
            Assert.All(
                factory.Logs.Messages,
                message => Assert.DoesNotContain(value, message, StringComparison.Ordinal)
            );
        }

        using var ready = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task WhenNotifyDoesNotRespond_ShouldCancelRequestAtExtendedHealthTimeoutAndReturnSafeFailure()
    {
        var cancelled = false;
        using var handler = new ControlledHandler(
            async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    throw new OperationCanceledException(PrivateData, token);
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        await using var factory = new HealthApplicationFactory("2100-01-01T00:00:00Z", handler);
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);

        using var response = await client.GetAsync("/health/all", TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(text);

        Assert.True(cancelled);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var notify = body.RootElement.GetProperty("results").GetProperty("Notify");
        Assert.Equal("Unhealthy", notify.GetProperty("status").GetString());
        Assert.Equal("Failed to connect to GOV.UK Notify.", notify.GetProperty("description").GetString());
        Assert.False(notify.TryGetProperty("exception", out _));
        Assert.False(notify.TryGetProperty("data", out _));
        Assert.DoesNotContain(PrivateData, text, StringComparison.Ordinal);
        Assert.All(
            factory.Logs.Messages,
            message => Assert.DoesNotContain(PrivateData, message, StringComparison.Ordinal)
        );
    }

    private sealed class HealthApplicationFactory(string? cutover, HttpMessageHandler handler)
        : WebApplicationFactory<Program>
    {
        public RecordingLogs Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(ConfigureBoundaries);
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["AWS_EMF_ENABLED"] = "false",
                    ["AnalyticsEventConsumer:ProcessingEnabled"] = "false",
                    ["AnalyticsEventConsumer:QueueUrl"] = "http://sqs.local/analytics",
                    ["NotificationCommandDelivery:QueueUrl"] = "http://sqs.local/commands.fifo",
                    ["CommandDlqAdministration:QueueUrl"] = "http://sqs.local/commands-dlq.fifo",
                    ["NotificationCommandDelivery:EvidenceDigestSecret"] = "health-test-evidence-secret",
                    ["NotificationCommandDelivery:RecipientLaneSecret"] = "health-test-recipient-secret",
                    ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
                    ["Mongo:DatabaseName"] = "health-test",
                    ["Notify:ApiKey"] = NotifyTestCredentials.ApiKey,
                    ["Notify:BaseAddress"] = "http://notify.local",
                };
                if (cutover is not null)
                    values["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = cutover;
                configuration.AddInMemoryCollection(values);
            });

            return base.CreateHost(builder);
        }

        private void ConfigureBoundaries(IServiceCollection services)
        {
            services.AddSingleton<ILoggerFactory>(_ =>
                LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs))
            );
            var backgroundServices = services
                .Where(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService)
                    && (
                        descriptor.ImplementationType == typeof(NotificationCommandConsumer)
                        || descriptor.ImplementationType == typeof(MongoMigrationService)
                    )
                )
                .ToArray();
            foreach (var service in backgroundServices)
                services.Remove(service);

            var completion = new MongoMigrationCompletion();
            completion.MarkCompleted();
            services.RemoveAll<MongoMigrationCompletion>();
            services.AddSingleton(completion);

            var sqs = Substitute.For<IAmazonSQS>();
            sqs.GetQueueAttributesAsync(Arg.Any<GetQueueAttributesRequest>(), Arg.Any<CancellationToken>())
                .Returns(new GetQueueAttributesResponse { HttpStatusCode = HttpStatusCode.OK });
            services.RemoveAll<IAmazonSQS>();
            services.AddSingleton(sqs);
            var mongo = Substitute.For<IMongoClient>();
            var database = Substitute.For<IMongoDatabase>();
            mongo.GetDatabase(Arg.Any<string>(), Arg.Any<MongoDatabaseSettings>()).Returns(database);
            database
                .RunCommandAsync<BsonDocument>(
                    Arg.Any<Command<BsonDocument>>(),
                    Arg.Any<ReadPreference>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(new BsonDocument("ok", 1));
            services.RemoveAll<IMongoClient>();
            services.AddSingleton(mongo);
            services
                .AddHttpClient<INotifyEmailClient, NotifyEmailClient>()
                .ConfigurePrimaryHttpMessageHandler(() => handler);
        }
    }

    private sealed class RecordingLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);

        public void Dispose() { }

        private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) => messages.Enqueue($"{formatter(state, exception)} {exception}");
        }
    }

    private sealed class ControlledHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _requestCount);

            return send(request, cancellationToken);
        }
    }
}
