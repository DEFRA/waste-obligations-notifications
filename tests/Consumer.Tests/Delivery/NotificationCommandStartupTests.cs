using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests.Delivery;

public sealed class NotificationCommandStartupTests
{
    private const string EvidenceSecret = "private-test-evidence-secret";
    private const string LaneSecret = "private-test-lane-secret";

    [Theory]
    [InlineData("EvidenceDigestSecret", "set-automatically-by-deployment-evidence-secret")]
    [InlineData("RecipientLaneSecret", "set-automatically-by-deployment-lane-secret")]
    [InlineData("EvidenceDigestSecret", " ")]
    [InlineData("RecipientLaneSecret", " ")]
    [InlineData("EmailDeliveryCutoverUtc", "set-automatically-when-deployed")]
    [InlineData("EmailDeliveryCutoverUtc", "invalid-private-cutover-marker")]
    [InlineData("EmailDeliveryCutoverUtc", "private-invalid-cutoverZ")]
    [InlineData("EmailDeliveryCutoverUtc", "2026-10-01T00:00:00+01:00")]
    [InlineData("EmailDeliveryCutoverUtc", "2026-10-01T00:00:00")]
    public async Task WhenEnabledWithInvalidConfiguration_ShouldFailStartupBeforeReceivingAndNotExposeValues(
        string field,
        string invalidValue
    )
    {
        var sqs = Substitute.For<IAmazonSQS>();
        var logger = Substitute.For<ILogger>();
        using var host = CreateHost(
            sqs,
            logger,
            true,
            new Dictionary<string, string?> { [$"NotificationCommandDelivery:{field}"] = invalidValue }
        );

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
        await sqs.DidNotReceive().ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
        var logged = string.Join("\n", logger.ReceivedCalls().Select(call => string.Join(" ", call.GetArguments())));
        var evidence = $"{exception}\n{logged}";
        Assert.DoesNotContain(EvidenceSecret, evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(LaneSecret, evidence, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(invalidValue))
            Assert.DoesNotContain(invalidValue, evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2100-01-01T00:00:00Z")]
    [InlineData("2100-01-01T00:00:00+00:00")]
    [InlineData("2100-01-01T00:00:00-00:00")]
    public async Task WhenEnabledWithConfiguredSecretsAndUtcCutover_ShouldStartAndReceive(string cutover)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sqs = Substitute.For<IAmazonSQS>();
        sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                received.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());

                return new ReceiveMessageResponse();
            });
        using var host = CreateHost(
            sqs,
            Substitute.For<ILogger>(),
            true,
            new Dictionary<string, string?> { ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = cutover }
        );

        await host.StartAsync(TestContext.Current.CancellationToken);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        await sqs.Received(1).ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenDisabledWithDeploymentPlaceholders_ShouldStartWithoutReceiving()
    {
        const string placeholder = "set-automatically-when-deployed";
        var sqs = Substitute.For<IAmazonSQS>();
        using var host = CreateHost(
            sqs,
            Substitute.For<ILogger>(),
            false,
            new Dictionary<string, string?>
            {
                ["NotificationCommandDelivery:EvidenceDigestSecret"] = placeholder,
                ["NotificationCommandDelivery:RecipientLaneSecret"] = placeholder,
                ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = placeholder,
            }
        );

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        await sqs.DidNotReceive().ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
    }

    private static IHost CreateHost(
        IAmazonSQS sqs,
        ILogger logger,
        bool processingEnabled,
        Dictionary<string, string?>? overrides = null
    )
    {
        var values = new Dictionary<string, string?>
        {
            ["NotificationCommandDelivery:ProcessingEnabled"] = processingEnabled.ToString(),
            ["NotificationCommandDelivery:QueueUrl"] = "http://localhost:4566/commands.fifo",
            ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = "2100-01-01T00:00:00Z",
            ["NotificationCommandDelivery:EvidenceDigestSecret"] = EvidenceSecret,
            ["NotificationCommandDelivery:RecipientLaneSecret"] = LaneSecret,
            ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
            ["Mongo:DatabaseName"] = "startup-test",
        };
        foreach (var entry in overrides ?? [])
            values[entry.Key] = entry.Value;

        var loggerProvider = Substitute.For<ILoggerProvider>();
        loggerProvider.CreateLogger(Arg.Any<string>()).Returns(logger);
        var readiness = new MongoMigrationReadiness();
        readiness.MarkCompleted();
        var runner = Substitute.For<IMongoMigrationRunner>();
        runner.CheckReadiness(Arg.Any<CancellationToken>()).Returns(true);

        return new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(values))
            .ConfigureLogging(logging => logging.AddProvider(loggerProvider))
            .ConfigureServices(
                (context, services) =>
                {
                    services.AddNotificationCommandDelivery(context.Configuration);
                    services.AddSingleton(sqs);
                    services.AddSingleton(readiness);
                    services.AddSingleton(runner);
                    services.AddSingleton(Substitute.For<IMongoMigrationLeaseService>());
                }
            )
            .Build();
    }
}
