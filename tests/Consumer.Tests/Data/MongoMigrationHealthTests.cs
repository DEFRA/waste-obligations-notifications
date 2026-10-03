using System.Net;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Startup;
using Defra.WasteObligations.Consumer.Utils.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests.Data;

public sealed class MongoMigrationHealthTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenCriticalMigrationsComplete_ShouldStartConsumersOnlyAfterSuccessfulHealthResponse(
        bool commandsEnabled
    )
    {
        var completion = new MongoMigrationCompletion();
        var receives = 0;
        var receiving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sqs = Substitute.For<IAmazonSQS>();
        sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (Interlocked.Increment(ref receives) == (commandsEnabled ? 2 : 1))
                    receiving.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());

                return new ReceiveMessageResponse();
            });
        await using var app = CreateApplication(completion, sqs, commandsEnabled);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        var startup = app.Services.GetRequiredService<ApplicationStartup>();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref receives));
        Assert.False(startup.IsStarted);
        if (commandsEnabled)
        {
            using var unavailable = await client.GetAsync("/health", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.False(startup.IsStarted);
            Assert.Equal(0, Volatile.Read(ref receives));
        }

        completion.MarkCompleted();
        using var extended = await client.GetAsync("/health/all", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, extended.StatusCode);
        Assert.False(startup.IsStarted);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref receives));

        using var ready = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        await receiving.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(startup.IsStarted);
        using var again = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(commandsEnabled ? 2 : 1, Volatile.Read(ref receives));
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WhenStoppedBeforeCriticalReadiness_ShouldCancelConsumersWithoutQueueEffects()
    {
        var sqs = Substitute.For<IAmazonSQS>();
        await using var app = CreateApplication(new MongoMigrationCompletion(), sqs, true);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await app.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(app.Services.GetRequiredService<ApplicationStartup>().IsStarted);
        await sqs.DidNotReceive().ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
        await sqs.DidNotReceive().DeleteMessageAsync(Arg.Any<DeleteMessageRequest>(), Arg.Any<CancellationToken>());
    }

    private static WebApplication CreateApplication(
        MongoMigrationCompletion completion,
        IAmazonSQS sqs,
        bool commandsEnabled
    )
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AnalyticsEventConsumer:ProcessingEnabled"] = "true",
                ["AnalyticsEventConsumer:QueueUrl"] = "http://localhost:4566/analytics",
                ["NotificationCommandDelivery:ProcessingEnabled"] = commandsEnabled.ToString(),
                ["NotificationCommandDelivery:QueueUrl"] = "http://localhost:4566/commands.fifo",
                ["NotificationCommandDelivery:EvidenceDigestSecret"] = "test-evidence-secret",
                ["NotificationCommandDelivery:RecipientLaneSecret"] = "test-lane-secret",
                ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
                ["Mongo:DatabaseName"] = "health-test",
                ["Notify:ApiKey"] = NotifyTestCredentials.ApiKey,
                ["AWS_EMF_ENABLED"] = "false",
            }
        );
        builder.Services.AddAnalyticsEventConsumer(builder.Configuration);
        builder.Services.AddNotificationCommandDelivery(builder.Configuration);
        foreach (
            var service in builder
                .Services.Where(service =>
                    service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType == typeof(MongoMigrationService)
                )
                .ToArray()
        )
            builder.Services.Remove(service);
        builder.Services.AddSingleton(completion);
        builder.Services.AddSingleton(sqs);
        builder.Services.AddHealth(builder.Configuration);
        builder.Services.Configure<HealthCheckServiceOptions>(options =>
        {
            // Exercise the real startup registration independently of live dependency checks.
            foreach (
                var registration in options
                    .Registrations.Where(item => item.Name != "MongoMigrationCompletion")
                    .ToArray()
            )
                options.Registrations.Remove(registration);
        });
        var app = builder.Build();
        app.MapHealth();

        return app;
    }
}
