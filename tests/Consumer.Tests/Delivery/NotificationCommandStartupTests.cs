using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Startup;
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
    [InlineData("QueueUrl", "set-automatically-when-deployed")]
    [InlineData("QueueUrl", "http://localhost:4566/not-fifo")]
    [InlineData("QueueUrl", "ftp://localhost/commands.fifo")]
    [InlineData("QueueUrl", "private-invalid-queue")]
    [InlineData("EvidenceDigestSecret", "set-automatically-by-deployment-evidence-secret")]
    [InlineData("RecipientLaneSecret", "set-automatically-by-deployment-lane-secret")]
    [InlineData("VisibilityTimeoutSeconds", "119")]
    [InlineData("CommandLeaseSeconds", "89")]
    [InlineData("NotifyTimeoutSeconds", "61")]
    [InlineData("EvidenceDigestSecret", " ")]
    [InlineData("RecipientLaneSecret", " ")]
    [InlineData("EmailDeliveryCutoverUtc", "set-automatically-when-deployed")]
    [InlineData("EmailDeliveryCutoverUtc", "invalid-private-cutover-marker")]
    [InlineData("EmailDeliveryCutoverUtc", "private-invalid-cutoverZ")]
    [InlineData("EmailDeliveryCutoverUtc", "2026-10-01T00:00:00+01:00")]
    [InlineData("EmailDeliveryCutoverUtc", "2026-10-01T00:00:00")]
    [InlineData("EmailDeliveryCutoverUtc", "00:00Z")]
    [InlineData("EmailDeliveryCutoverUtc", "")]
    [InlineData("EmailDeliveryCutoverUtc", " ")]
    public async Task WhenConfigurationInvalid_ShouldFailStartupBeforeReceivingAndNotExposeValues(
        string field,
        string invalidValue
    )
    {
        var sqs = Substitute.For<IAmazonSQS>();
        var logger = Substitute.For<ILogger>();
        using var host = CreateHost(
            sqs,
            logger,
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
    [InlineData(null)]
    [InlineData("2100-01-01T00:00:00Z")]
    [InlineData("2100-01-01T00:00:00+00:00")]
    [InlineData("2100-01-01T00:00:00-00:00")]
    public async Task WhenSecretsAndUtcCutoverConfigured_ShouldStartAndReceive(string? cutover)
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
            new Dictionary<string, string?> { ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = cutover }
        );

        await host.StartAsync(TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<ApplicationStartup>().MarkStarted();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        await sqs.Received(1).ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenJsonNullCutoverConfigured_ShouldOverrideConfiguredValueAndReceive()
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
        using var host = CreateHost(sqs, Substitute.For<ILogger>(), jsonNullCutover: true);

        await host.StartAsync(TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<ApplicationStartup>().MarkStarted();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Null(
            host.Services.GetRequiredService<
                IOptions<NotificationCommandDeliveryOptions>
            >().Value.EmailDeliveryCutoverUtc
        );
        await host.StopAsync(TestContext.Current.CancellationToken);
        await sqs.Received(1).ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenRetiredProcessingFlagIsFalse_ShouldStillReceiveAfterStartup()
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
            new Dictionary<string, string?> { ["NotificationCommandDelivery:ProcessingEnabled"] = "false" }
        );
        await host.StartAsync(TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<ApplicationStartup>().MarkStarted();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        await sqs.Received(1).ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2100-01-01T00:00:00Z")]
    public async Task WhenNotifyCredentialsInvalid_ShouldFailBeforeReceivingWithoutExposingKey(string? cutover)
    {
        const string invalidKey = "private-invalid-api-key";
        var sqs = Substitute.For<IAmazonSQS>();
        using var host = CreateHost(
            sqs,
            Substitute.For<ILogger>(),
            new()
            {
                ["Notify:ApiKey"] = invalidKey,
                ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = cutover,
                ["NotificationCommandDelivery:ProcessingEnabled"] = "false",
            }
        );

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.DoesNotContain(invalidKey, exception.ToString(), StringComparison.Ordinal);
        await sqs.DidNotReceive().ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Notify:BaseAddress", "private-invalid-notify-url")]
    [InlineData("NotificationCommandDelivery:CommandLeaseSeconds", "89")]
    [InlineData("NotificationCommandDelivery:VisibilityTimeoutSeconds", "119")]
    public async Task WhenNullCutoverAndRetiredFlagConfigured_ShouldStillValidateUrlAndSendBudget(
        string setting,
        string invalidValue
    )
    {
        var sqs = Substitute.For<IAmazonSQS>();
        var logger = Substitute.For<ILogger>();
        using var host = CreateHost(
            sqs,
            logger,
            new()
            {
                ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = null,
                ["NotificationCommandDelivery:ProcessingEnabled"] = "false",
                [setting] = invalidValue,
            }
        );

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        var logged = string.Join("\n", logger.ReceivedCalls().Select(call => string.Join(" ", call.GetArguments())));
        Assert.DoesNotContain(invalidValue, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(invalidValue, logged, StringComparison.Ordinal);
        await sqs.DidNotReceive().ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("2100-01-01T00:00:00Z", false)]
    [InlineData("2100-01-01T00:00:00Z", true)]
    public async Task WhenNotifyKeyDoesNotMeetSdkShape_ShouldRejectItBeforeReceiving(
        string? cutover,
        bool containsSpace
    )
    {
        var invalidKey = containsSpace
            ? $"private prefix-{NotifyTestCredentials.ApiKey}"
            : NotifyTestCredentials.ApiKey[^73..];
        var sqs = Substitute.For<IAmazonSQS>();
        var logger = Substitute.For<ILogger>();
        using var host = CreateHost(
            sqs,
            logger,
            new() { ["Notify:ApiKey"] = invalidKey, ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = cutover }
        );

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains("Notify ApiKey", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(invalidKey, exception.ToString(), StringComparison.Ordinal);
        await sqs.DidNotReceive().ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
        var logged = string.Join("\n", logger.ReceivedCalls().Select(call => string.Join(" ", call.GetArguments())));
        Assert.DoesNotContain(invalidKey, logged, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("private-address@example.com")]
    [InlineData("Submitted")]
    [InlineData("private template")]
    public async Task WhenDiagnosticCategoryConfigurationIsInvalid_ShouldRejectItWithoutExposingValues(string label)
    {
        var sqs = Substitute.For<IAmazonSQS>();
        using var host = CreateHost(
            sqs,
            Substitute.For<ILogger>(),
            new() { ["NotificationCommandDelivery:DiagnosticNotificationTypes:0"] = label }
        );

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains("DiagnosticNotificationTypes", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(label, exception.Message, StringComparison.Ordinal);
        await sqs.DidNotReceive().ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenDiagnosticCategoryConfigurationIsUnbounded_ShouldFailBeforeReceiving()
    {
        var sqs = Substitute.For<IAmazonSQS>();
        var labels = Enumerable
            .Range(0, 33)
            .ToDictionary(
                index => $"NotificationCommandDelivery:DiagnosticNotificationTypes:{index}",
                _ => (string?)"submitted"
            );
        using var host = CreateHost(sqs, Substitute.For<ILogger>(), labels);

        await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        await sqs.DidNotReceive().ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
    }

    private static IHost CreateHost(
        IAmazonSQS sqs,
        ILogger logger,
        Dictionary<string, string?>? overrides = null,
        bool jsonNullCutover = false
    )
    {
        var values = new Dictionary<string, string?>
        {
            ["AWS_EMF_ENABLED"] = "false",
            ["NotificationCommandDelivery:QueueUrl"] = "http://localhost:4566/commands.fifo",
            ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = "2100-01-01T00:00:00Z",
            ["NotificationCommandDelivery:EvidenceDigestSecret"] = EvidenceSecret,
            ["NotificationCommandDelivery:RecipientLaneSecret"] = LaneSecret,
            ["Notify:ApiKey"] = NotifyTestCredentials.ApiKey,
            ["Notify:BaseAddress"] = "http://notify.local",
            ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
            ["Mongo:DatabaseName"] = "startup-test",
        };
        foreach (var entry in overrides ?? [])
        {
            if (entry.Value is null)
                values.Remove(entry.Key);
            else
                values[entry.Key] = entry.Value;
        }

        var loggerProvider = Substitute.For<ILoggerProvider>();
        loggerProvider.CreateLogger(Arg.Any<string>()).Returns(logger);
        var readiness = new MongoMigrationCompletion();
        readiness.MarkCompleted();
        var runner = Substitute.For<IMongoMigrationRunner>();
        runner.CheckCompletion(Arg.Any<CancellationToken>()).Returns(true);

        return new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(values);
                if (jsonNullCutover)
                    configuration.AddJsonStream(
                        new MemoryStream(
                            Encoding.UTF8.GetBytes(
                                """{"NotificationCommandDelivery":{"EmailDeliveryCutoverUtc":null}}"""
                            )
                        )
                    );
            })
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
