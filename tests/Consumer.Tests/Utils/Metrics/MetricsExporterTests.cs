using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Amazon.CloudWatch.EMF.Environment;
using Amazon.CloudWatch.EMF.Model;
using Amazon.CloudWatch.EMF.Sink;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Utils.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using CommandMetrics = Defra.WasteObligations.Consumer.Utils.Metrics.Metrics;

namespace Defra.WasteObligations.Consumer.Tests.Utils.Metrics;

public sealed class MetricsExporterTests
{
    private const string PrivateValues = "private-key recipient@example.com private-body template-1";

    [Theory]
    [InlineData("none")]
    [InlineData("export")]
    [InlineData("startup")]
    public async Task WhenTheCommandConsumerStarts_ShouldExportItsFirstCommandWithoutExportFailuresAffectingDelivery(
        string failure
    )
    {
        var sink = new RecordingSink { ExportFailure = failure == "export" };
        var environment = new ControlledEnvironment(sink);
        var provider = Substitute.For<Func<CancellationToken, IEnvironment>>();
        provider
            .Invoke(Arg.Any<CancellationToken>())
            .Returns(_ => failure == "startup" ? throw new InvalidOperationException(PrivateValues) : environment);
        var logs = new RecordingLogs();
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sqs = Substitute.For<IAmazonSQS>();
        sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromResult(new ReceiveMessageResponse { Messages = [CommandMessage()] }),
                async call =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());

                    return new ReceiveMessageResponse();
                }
            );
        sqs.DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DeleteMessageResponse())
            .AndDoes(_ => deleted.TrySetResult());
        var store = Substitute.For<INotificationDeliveryRecordStore>();
        store
            .Claim(Arg.Any<NotificationCommand>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(DeliveryClaimResult.TerminalDuplicate);
        var factory = Substitute.For<INotificationDeliveryRecordStoreFactory>();
        factory.GetRecordStore().Returns(store);
        var readiness = new MongoMigrationReadiness();
        readiness.MarkCompleted();
        var runner = Substitute.For<IMongoMigrationRunner>();
        runner.CheckReadiness(Arg.Any<CancellationToken>()).Returns(true);
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.AddProvider(logs))
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(Configuration()))
            .ConfigureServices(
                (context, services) =>
                {
                    services.AddNotificationCommandDelivery(context.Configuration);
                    services.AddSingleton(provider);
                    services.AddSingleton(sqs);
                    services.AddSingleton(factory);
                    services.AddSingleton(readiness);
                    services.AddSingleton(runner);
                    services.AddSingleton(Substitute.For<IMongoMigrationLeaseService>());
                }
            )
            .Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        if (failure != "none")
        {
            Assert.Empty(sink.Documents);
            Assert.Contains(logs.Messages, message => message.Contains("EMF failure", StringComparison.Ordinal));
        }
        else
            Assert.Equal(5, sink.Documents.Count);
        foreach (var value in PrivateValues.Split(' '))
            Assert.DoesNotContain(value, string.Join(' ', logs.Messages), StringComparison.Ordinal);
        foreach (var document in sink.Documents)
        {
            using var json = JsonDocument.Parse(document);
            var directive = Assert.Single(
                json.RootElement.GetProperty("_aws").GetProperty("CloudWatchMetrics").EnumerateArray()
            );
            Assert.Equal("notifications-test", directive.GetProperty("Namespace").GetString());
            Assert.Single(directive.GetProperty("Metrics").EnumerateArray());
            Assert.Equal("other", json.RootElement.GetProperty("NotificationType").GetString());
            foreach (var value in PrivateValues.Split(' '))
                Assert.DoesNotContain(value, document, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WhenCommandInstrumentsPublish_ShouldEmitExactlyOneRealEmfDocumentPerMeasurementWithApprovedDimensions()
    {
        var sink = new RecordingSink();
        using var host = CreateMetricsHost(sink);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var metrics = host.Services.GetRequiredService<INotificationCommandMetrics>();

        metrics.RecordReceived("submitted");
        metrics.RecordOutcome("submitted", "delivery-accepted");
        metrics.RecordClaim("submitted", DeliveryClaimResult.Claimed, 125.5);
        metrics.RecordSendAccepted("submitted");
        metrics.RecordSendFailure("submitted");
        metrics.RecordSendDuration("submitted", 75.25);
        metrics.RecordDuplicate("submitted");
        await host.StopAsync(TestContext.Current.CancellationToken);

        var expected = new Dictionary<string, (double Value, string Unit, string? Outcome)>
        {
            ["NotificationCommandReceived"] = (1, "Count", null),
            ["NotificationCommandOutcome"] = (1, "Count", "delivery-accepted"),
            ["NotificationCommandLeaseClaim"] = (1, "Count", "claimed"),
            ["NotificationCommandLeaseClaimDuration"] = (125.5, "Milliseconds", "claimed"),
            ["NotificationCommandNotifySendAccepted"] = (1, "Count", null),
            ["NotificationCommandNotifySendFailure"] = (1, "Count", null),
            ["NotificationCommandNotifySendDuration"] = (75.25, "Milliseconds", null),
            ["NotificationCommandDuplicateSuppressed"] = (1, "Count", null),
        };
        Assert.Equal(expected.Count, sink.Documents.Count);
        var observed = new HashSet<string>();
        foreach (var document in sink.Documents)
        {
            using var json = JsonDocument.Parse(document);
            var root = json.RootElement;
            var directive = Assert.Single(root.GetProperty("_aws").GetProperty("CloudWatchMetrics").EnumerateArray());
            var metric = Assert.Single(directive.GetProperty("Metrics").EnumerateArray());
            var name = metric.GetProperty("Name").GetString()!;
            Assert.True(observed.Add(name));
            var (value, unit, outcome) = expected[name];
            Assert.Equal("notifications-test", directive.GetProperty("Namespace").GetString());
            Assert.Equal(unit, metric.GetProperty("Unit").GetString());
            Assert.Equal(value, root.GetProperty(name).GetDouble());
            Assert.Equal("waste-obligations-notifications", root.GetProperty("Service").GetString());
            Assert.Equal("submitted", root.GetProperty("NotificationType").GetString());
            string[] dimensions = outcome is null
                ? ["NotificationType", "Service"]
                : ["NotificationType", "Outcome", "Service"];
            Assert.Equal(
                dimensions,
                Assert
                    .Single(directive.GetProperty("Dimensions").EnumerateArray())
                    .EnumerateArray()
                    .Select(dimension => dimension.GetString())
                    .Order()
            );
            Assert.Equal(
                dimensions.Concat(["_aws", name]).Order(),
                root.EnumerateObject().Select(property => property.Name).Order()
            );
            if (outcome is not null)
                Assert.Equal(outcome, root.GetProperty("Outcome").GetString());
            foreach (var privateValue in PrivateValues.Split(' '))
                Assert.DoesNotContain(privateValue, document, StringComparison.Ordinal);
        }
        Assert.Equal(1, sink.ShutdownCalls);
    }

    [Fact]
    public async Task WhenExportIsDisabled_ShouldNeitherResolveTheSdkEnvironmentNorExportOrShutdownASink()
    {
        var sink = new RecordingSink();
        var factory = Substitute.For<Func<CancellationToken, IEnvironment>>();
        factory.Invoke(Arg.Any<CancellationToken>()).Returns(_ => throw new InvalidOperationException(PrivateValues));
        using var host = CreateMetricsHost(
            sink,
            new() { ["AWS_EMF_ENABLED"] = "false", ["AWS_EMF_NAMESPACE"] = "private invalid namespace@example.com" },
            factory
        );

        await host.StartAsync(TestContext.Current.CancellationToken);
        host.Services.GetRequiredService<INotificationCommandMetrics>().RecordReceived("submitted");
        await host.StopAsync(TestContext.Current.CancellationToken);

        factory.DidNotReceive().Invoke(Arg.Any<CancellationToken>());
        Assert.Empty(sink.Documents);
        Assert.Equal(0, sink.ShutdownCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("set-automatically-when-deployed")]
    [InlineData("private namespace@example.com")]
    public async Task WhenEnabledNamespaceIsMissingOrInvalid_ShouldFailBeforeResolvingTheSdkWithoutExposingValues(
        string? value
    )
    {
        var factory = Substitute.For<Func<CancellationToken, IEnvironment>>();
        using var host = CreateMetricsHost(
            new(),
            new() { ["AWS_EMF_ENABLED"] = null, ["AWS_EMF_NAMESPACE"] = value },
            factory
        );

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        factory.DidNotReceive().Invoke(Arg.Any<CancellationToken>());
        if (value is not null)
            Assert.DoesNotContain(value, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task WhenLocalNamespaceIsMissing_ShouldUseTheNotificationsNamespaceFallback(string? value)
    {
        var sink = new RecordingSink();
        using var host = CreateMetricsHost(
            sink,
            new() { ["AWS_EMF_NAMESPACE"] = value, ["AWS_EMF_ENVIRONMENT"] = "Local" }
        );
        await host.StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<INotificationCommandMetrics>().RecordReceived("submitted");
        await host.StopAsync(TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(Assert.Single(sink.Documents));
        var directive = Assert.Single(
            json.RootElement.GetProperty("_aws").GetProperty("CloudWatchMetrics").EnumerateArray()
        );
        Assert.Equal("Defra.WasteObligationsNotifications", directive.GetProperty("Namespace").GetString());
    }

    [Fact]
    public async Task WhenTwoHostsShareAMeterName_ShouldExportOnlyTheirOwnMeasurementsAndStopIndependently()
    {
        var firstSink = new RecordingSink();
        var secondSink = new RecordingSink();
        using var first = CreateMetricsHost(firstSink, new() { ["AWS_EMF_NAMESPACE"] = "first-host" });
        using var second = CreateMetricsHost(secondSink, new() { ["AWS_EMF_NAMESPACE"] = "second-host" });
        await first.StartAsync(TestContext.Current.CancellationToken);
        await second.StartAsync(TestContext.Current.CancellationToken);
        var firstMetrics = first.Services.GetRequiredService<INotificationCommandMetrics>();
        var secondMetrics = second.Services.GetRequiredService<INotificationCommandMetrics>();
        using var foreignMeter = new Meter(CommandMetrics.MeterName);

        firstMetrics.RecordReceived("submitted");
        foreignMeter.CreateCounter<long>("foreign-counter", "COUNT").Add(1);
        Assert.Single(firstSink.Documents);
        Assert.Empty(secondSink.Documents);
        await first.StopAsync(TestContext.Current.CancellationToken);
        firstMetrics.RecordReceived("submitted");
        secondMetrics.RecordReceived("submitted");
        first.Dispose();
        secondMetrics.RecordSendAccepted("submitted");
        await second.StopAsync(TestContext.Current.CancellationToken);

        Assert.Single(firstSink.Documents);
        Assert.Equal(2, secondSink.Documents.Count);
        Assert.All(firstSink.Documents, document => Assert.Contains("first-host", document, StringComparison.Ordinal));
        Assert.All(
            secondSink.Documents,
            document => Assert.Contains("second-host", document, StringComparison.Ordinal)
        );
        Assert.Equal(1, firstSink.ShutdownCalls);
        Assert.Equal(1, secondSink.ShutdownCalls);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("fault")]
    [InlineData("never")]
    [InlineData("late-fault")]
    [InlineData("cancel")]
    public async Task WhenSinkShutdownFailsOrOutlivesItsBudget_ShouldDetachAndNotAffectAnotherHost(string failure)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingSink
        {
            OnShutdown = () =>
                failure switch
                {
                    "throw" => throw new InvalidOperationException(PrivateValues),
                    "fault" => Task.FromException(new InvalidOperationException(PrivateValues)),
                    _ => completion.Task,
                },
        };
        var logs = new RecordingLogs();
        using var host = CreateMetricsHost(sink, new() { ["AWS_EMF_SHUTDOWN_TIMEOUT_SECONDS"] = "1" }, logs: logs);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var metrics = host.Services.GetRequiredService<INotificationCommandMetrics>();
        metrics.RecordReceived("submitted");
        using var stop = new CancellationTokenSource();
        if (failure == "cancel")
            await stop.CancelAsync();

        var stopping = host.StopAsync(stop.Token);
        metrics.RecordReceived("submitted");
        await stopping.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var nextSink = new RecordingSink();
        using var next = CreateMetricsHost(nextSink);
        await next.StartAsync(TestContext.Current.CancellationToken);
        if (failure == "late-fault")
            completion.TrySetException(new InvalidOperationException(PrivateValues));
        else
            completion.TrySetResult();
        next.Services.GetRequiredService<INotificationCommandMetrics>().RecordReceived("submitted");
        await next.StopAsync(TestContext.Current.CancellationToken);

        Assert.Single(sink.Documents);
        Assert.Single(nextSink.Documents);
        Assert.Equal(1, sink.ShutdownCalls);
        Assert.Contains(
            logs.Messages,
            message => message.Contains("EMF failure during shutdown", StringComparison.Ordinal)
        );
        foreach (var value in PrivateValues.Split(' '))
            Assert.DoesNotContain(value, string.Join(' ', logs.Messages), StringComparison.Ordinal);
    }

    private static IHost CreateMetricsHost(
        RecordingSink sink,
        Dictionary<string, string?>? overrides = null,
        Func<CancellationToken, IEnvironment>? factory = null,
        RecordingLogs? logs = null
    )
    {
        var values = Configuration();
        foreach (var entry in overrides ?? [])
        {
            if (entry.Value is null)
                values.Remove(entry.Key);
            else
                values[entry.Key] = entry.Value;
        }
        if (factory is null)
        {
            factory = Substitute.For<Func<CancellationToken, IEnvironment>>();
            factory.Invoke(Arg.Any<CancellationToken>()).Returns(new ControlledEnvironment(sink));
        }

        return new HostBuilder()
            .ConfigureLogging(logging => logging.AddProvider(logs ?? new RecordingLogs()))
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(values))
            .ConfigureServices(
                (context, services) =>
                {
                    services.AddNotificationCommandMetrics();
                    services.AddNotificationCommandEmfExport(context.Configuration);
                    services.AddSingleton(factory);
                }
            )
            .Build();
    }

    private static Dictionary<string, string?> Configuration() =>
        new()
        {
            ["AWS_EMF_ENABLED"] = "true",
            ["AWS_EMF_NAMESPACE"] = "notifications-test",
            ["AWS_EMF_ENVIRONMENT"] = "Agent",
            ["NotificationCommandDelivery:ProcessingEnabled"] = "true",
            ["NotificationCommandDelivery:QueueUrl"] = "commands.fifo",
            ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = "2026-09-29T00:00:00Z",
            ["NotificationCommandDelivery:EvidenceDigestSecret"] = "test-evidence-secret",
            ["NotificationCommandDelivery:RecipientLaneSecret"] = "test-lane-secret",
            ["Notify:ApiKey"] = NotifyTestCredentials.ApiKey,
            ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
            ["Mongo:DatabaseName"] = "metrics-startup-test",
        };

    private static Message CommandMessage() =>
        new()
        {
            MessageId = "message-1",
            ReceiptHandle = "receipt",
            Body = JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    idempotencyKey = "private-key",
                    actionOccurredAtUtc = "2026-09-29T00:00:00Z",
                    notificationType = PrivateValues,
                    emailAddress = "recipient@example.com",
                    templateId = "template-1",
                    personalisation = new { body = "private-body" },
                }
            ),
        };

    private sealed class ControlledEnvironment(ISink sink) : IEnvironment
    {
        public bool Probe() => true;

        public string Name => "test-service";
        public string Type => "test-environment";
        public string LogGroupName => "test-log-group";
        public ISink Sink { get; set; } = sink;

        public void ConfigureContext(MetricsContext context) =>
            throw new InvalidOperationException("Environment decoration must not run while delivering a command");
    }

    private sealed class RecordingSink : ISink
    {
        public ConcurrentQueue<string> Documents { get; } = new();
        public bool ExportFailure { get; init; }
        public Func<Task>? OnShutdown { get; init; }
        public int ShutdownCalls { get; private set; }

        public void Accept(MetricsContext context)
        {
            if (ExportFailure)
                throw new InvalidOperationException(PrivateValues);
            foreach (var document in context.Serialize())
                Documents.Enqueue(document);
        }

        public Task Shutdown()
        {
            ShutdownCalls++;

            return OnShutdown?.Invoke() ?? Task.CompletedTask;
        }
    }

    private sealed class RecordingLogs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Messages.Enqueue($"{formatter(state, exception)} {exception}");

        public void Dispose() { }
    }
}
