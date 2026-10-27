using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Startup;
using Defra.WasteObligations.Consumer.Utils.Metrics;
using Elastic.CommonSchema.Serilog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.Core;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using LoggerConfiguration = Serilog.LoggerConfiguration;

namespace Defra.WasteObligations.Consumer.Tests.Delivery;

public sealed class NotificationCommandConsumerTests : IDisposable
{
    private const string CommandIdempotencyKey = "command-key-1";
    private const string EmailAddress = "recipient@example.com";
    private const string Personalisation = "secret personalisation";
    private const string QueueUrl = "http://localhost:4566/000000000000/commands.fifo";
    private const string ReceiptHandle = "receipt-handle-1";
    private readonly ServiceProvider _metricServices = new ServiceCollection()
        .AddNotificationCommandMetrics()
        .BuildServiceProvider();

    public void Dispose() => _metricServices.Dispose();

    [Theory]
    [InlineData("accepted", "delivery-accepted", 125.25)]
    [InlineData("accepted-metric-failed", "delivery-accepted", 125.25)]
    [InlineData("acceptance-store-failed", "delivery-accepted", 125.25)]
    [InlineData("notify-failed", null, null)]
    [InlineData("duplicate", null, null)]
    [InlineData("suppressed", "delivery-suppressed", 127.5)]
    [InlineData("suppressed-metric-failed", "delivery-suppressed", 127.5)]
    [InlineData("before-cutover", "delivery-suppressed", 127.5)]
    [InlineData("suppressed-duplicate", null, null)]
    [InlineData("suppressed-failed", null, null)]
    [InlineData("suppressed-conflict", null, null)]
    [InlineData("suppressed-active-claim", null, null)]
    [InlineData("suppressed-future-action", null, null)]
    [InlineData("future-action", null, null)]
    public async Task Start_ShouldMeasureActionToSuppressionOrConfirmedNotifyAcceptance(
        string scenario,
        string? expectedOutcome,
        double? expectedOutcomeSeconds
    )
    {
        var clock = Substitute.For<TimeProvider>();
        var startedAt = new DateTimeOffset(2026, 10, 1, 10, 2, 0, TimeSpan.Zero);
        clock.GetUtcNow().Returns(startedAt);
        var measurements = new List<(string Name, double Value, string? Outcome)>();
        var meter = _metricServices
            .GetRequiredService<IMeterFactory>()
            .Create(global::Defra.WasteObligations.Consumer.Utils.Metrics.Metrics.MeterName);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter == meter && instrument.Name == MetricNames.NotificationCommandOutcomeLatency)
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) =>
            {
                measurements.Add(
                    (
                        instrument.Name,
                        value,
                        tags.ToArray().SingleOrDefault(tag => tag.Key == MetricTags.Outcome).Value as string
                    )
                );
                if (scenario.EndsWith("metric-failed", StringComparison.Ordinal))
                    throw new InvalidOperationException("private telemetry detail");
            }
        );
        listener.Start();

        var sqs = Substitute.For<IAmazonSQS>();
        var actionTime = scenario.EndsWith("future-action", StringComparison.Ordinal)
            ? "2026-10-02T10:00:00Z"
            : "2026-10-01T10:00:00Z";
        var receivedAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receive = MessageThenWait(CreateMessage(CommandBody(actionTime)));
        var receives = 0;
        sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (Interlocked.Increment(ref receives) > 1)
                    receivedAgain.TrySetResult();

                return receive(call);
            });
        var store = Substitute.For<INotificationDeliveryRecordStore>();
        store
            .RecordSuppression(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ =>
            {
                clock.GetUtcNow().Returns(startedAt.AddSeconds(7.5));

                return scenario switch
                {
                    "suppressed-duplicate" => Task.FromResult(SuppressionClaimResult.TerminalDuplicate),
                    "suppressed-conflict" => Task.FromResult(SuppressionClaimResult.Conflict),
                    "suppressed-active-claim" => Task.FromResult(SuppressionClaimResult.ActiveClaim),
                    "suppressed-failed" => Task.FromException<SuppressionClaimResult>(new InvalidOperationException()),
                    _ => Task.FromResult(SuppressionClaimResult.Recorded),
                };
            });
        store
            .Claim(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(scenario == "duplicate" ? DeliveryClaimResult.TerminalDuplicate : DeliveryClaimResult.Claimed);
        store
            .RecordAcceptance(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<string>(),
                Arg.Any<NotifyAcceptance>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ =>
            {
                clock.GetUtcNow().Returns(startedAt.AddMinutes(1));

                return scenario != "acceptance-store-failed";
            });
        var notify = Substitute.For<INotifyEmailClient>();
        notify
            .Send(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ =>
            {
                clock.GetUtcNow().Returns(startedAt.AddSeconds(5.25));

                return scenario == "notify-failed"
                    ? Task.FromException<NotifyAcceptance>(new InvalidOperationException())
                    : Task.FromResult(
                        new NotifyAcceptance("01234567-89ab-cdef-0123-456789abcdef", "reference", "template-1", 1)
                    );
            });
        var logger = new RecordingLogger<NotificationCommandConsumer>();
        var cutover = scenario switch
        {
            _ when scenario.StartsWith("suppressed", StringComparison.Ordinal) => null,
            "before-cutover" => "2026-10-02T00:00:00Z",
            _ => "2026-09-29T00:00:00Z",
        };
        using var subject = CreateSubject(sqs, store, logger, cutover: cutover, notify: notify, timeProvider: clock);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await receivedAgain.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        if (scenario.EndsWith("metric-failed", StringComparison.Ordinal))
        {
            await sqs.Received(1).DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>());
            if (scenario == "accepted-metric-failed")
            {
                await store
                    .Received(1)
                    .RecordAcceptance(
                        Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                        Arg.Any<string>(),
                        Arg.Any<NotifyAcceptance>(),
                        Arg.Any<CancellationToken>()
                    );
            }
            Assert.DoesNotContain(
                logger.Messages,
                message => message.Contains("private telemetry detail", StringComparison.Ordinal)
            );
            Assert.Empty(logger.Exceptions);
        }

        var outcomeMeasurements = measurements
            .Where(value => value.Name == MetricNames.NotificationCommandOutcomeLatency)
            .ToArray();
        if (expectedOutcomeSeconds is { } expectedLatency)
        {
            var measurement = Assert.Single(outcomeMeasurements);
            Assert.Equal(expectedLatency, measurement.Value);
            Assert.Equal(expectedOutcome, measurement.Outcome);
        }
        else
            Assert.Empty(outcomeMeasurements);
    }

    [Theory]
    [InlineData("2026-09-28T10:00:00")]
    [InlineData("2026-09-28T10:00:00+01:00")]
    public async Task Start_WhenActionTimestampLacksExplicitUtc_ShouldNotRecordOrDelete(string timestamp)
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(MessageThenWait(CreateMessage(CommandBody(timestamp))));
        var recordStore = Substitute.For<INotificationDeliveryRecordStore>();
        var logger = new RecordingLogger<NotificationCommandConsumer>();
        using var subject = CreateSubject(sqsClient, recordStore, logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForMessage("Notification command consumption failed", TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        Assert.Contains(
            logger.Messages,
            message => message.Contains("invalid-command (JsonException)", StringComparison.Ordinal)
        );

        await recordStore
            .DidNotReceive()
            .RecordSuppression(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<CancellationToken>()
            );
        await sqsClient
            .DidNotReceive()
            .DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(
            logger.Exceptions,
            exception => exception.Message.Contains(timestamp, StringComparison.Ordinal)
        );
    }

    [Theory]
    [InlineData("2026-09-28T10:00:00Z", null, true)]
    [InlineData("2026-09-29T00:00:00Z", null, true)]
    [InlineData("2101-01-01T00:00:00Z", null, true)]
    [InlineData("2026-09-28T10:00:00.1229999Z", "2026-09-28T10:00:00.1234567Z", true)]
    [InlineData("2026-09-28T10:00:00.1234567+00:00", "2026-09-28T10:00:00.1234567Z", false)]
    [InlineData("2026-09-28T10:00:00.12345676Z", "2026-09-28T10:00:00.12345676Z", false)]
    [InlineData("2026-09-28T10:00:00.123Z", "2026-09-28T10:00:00.12399996Z", false)]
    public async Task Start_WhenActionIsBeforeOrAtCutoverAtMongoPrecision_ShouldPreserveBoundary(
        string timestamp,
        string? cutover,
        bool suppressed
    )
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(MessageThenWait(CreateMessage(CommandBody(timestamp))));
        var recordStore = Substitute.For<INotificationDeliveryRecordStore>();
        var logger = new RecordingLogger<NotificationCommandConsumer>();
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sqsClient
            .DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>())
            .Returns(new DeleteMessageResponse())
            .AndDoes(_ => deleted.TrySetResult());
        var notify = Substitute.For<INotifyEmailClient>();
        notify
            .Send(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new NotifyAcceptance("01234567-89ab-cdef-0123-456789abcdef", "reference", "template-1", 1));
        recordStore
            .Claim(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(DeliveryClaimResult.Claimed);
        recordStore
            .RecordAcceptance(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<string>(),
                Arg.Any<NotifyAcceptance>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);
        using var subject = CreateSubject(sqsClient, recordStore, logger, cutover: cutover, notify: notify);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        await recordStore
            .Received(suppressed ? 1 : 0)
            .RecordSuppression(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<CancellationToken>()
            );
        var outcomeMeaning = (suppressed, cutover) switch
        {
            (true, null) => "suppressed because cutover is unset",
            (true, _) => "suppressed because action precedes cutover",
            _ => "email request accepted by Notify; acceptance recorded",
        };
        Assert.Contains(logger.Messages, message => message.Contains(outcomeMeaning, StringComparison.Ordinal));
        await sqsClient.Received(1).DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>());
        await notify
            .Received(suppressed ? 0 : 1)
            .Send(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_WhenIdempotencyKeyIsInvalid_ShouldNotRecordOrDeleteOrExposeIt(bool overlong)
    {
        var key = overlong ? new string('k', 129) : "key with space";
        var body = JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                idempotencyKey = key,
                actionOccurredAtUtc = "2026-09-28T10:00:00Z",
                notificationType = "declaration-submitted",
                emailAddress = EmailAddress,
                templateId = "template-1",
                personalisation = new { body = Personalisation },
            }
        );
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(MessageThenWait(CreateMessage(body)));
        var recordStore = Substitute.For<INotificationDeliveryRecordStore>();
        var logger = new RecordingLogger<NotificationCommandConsumer>();
        using var subject = CreateSubject(sqsClient, recordStore, logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForMessage("Notification command consumption failed", TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        await recordStore
            .DidNotReceive()
            .RecordSuppression(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<CancellationToken>()
            );
        await sqsClient
            .DidNotReceive()
            .DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(logger.Messages, message => message.Contains(key, StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Exceptions,
            exception => exception.Message.Contains(key, StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task Start_WhenDirectlyConfiguredWithInvalidCutover_ShouldFailBeforeReceiving()
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        using var subject = CreateSubject(
            sqsClient,
            Substitute.For<INotificationDeliveryRecordStore>(),
            cutover: "invalid-cutover"
        );

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => subject.ExecuteTask!);

        await sqsClient
            .DidNotReceive()
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_WhenReceiveSucceedsOrIsEmpty_ShouldImmediatelyReceiveAgain(bool hasCommand)
    {
        var receivedAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = false;
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                deleted = true;

                return new DeleteMessageResponse();
            });
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ =>
                    Task.FromResult(
                        new ReceiveMessageResponse
                        {
                            Messages = hasCommand ? [CreateMessage(CommandBody("2026-09-28T10:00:00Z"))] : [],
                        }
                    ),
                call =>
                {
                    Assert.Equal(1, call.Arg<ReceiveMessageRequest>().MaxNumberOfMessages);
                    Assert.Equal(hasCommand, deleted);
                    receivedAgain.TrySetResult();

                    return WaitForReceiveCancellation(call.Arg<CancellationToken>());
                }
            );
        var recordStore = Substitute.For<INotificationDeliveryRecordStore>();
        recordStore
            .RecordSuppression(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(SuppressionClaimResult.Recorded);
        using var subject = CreateSubject(sqsClient, recordStore, pollIntervalSeconds: 300);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await receivedAgain.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        await sqsClient.Received(2).ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
        await sqsClient
            .Received(hasCommand ? 1 : 0)
            .DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_WhenReceiveOrProcessingFails_ShouldWarnAndBackOffBeforeRetry(bool receiveFails)
    {
        var receivedAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ =>
                    receiveFails
                        ? Task.FromException<ReceiveMessageResponse>(new InvalidOperationException("Receive failed"))
                        : Task.FromResult(new ReceiveMessageResponse { Messages = [CreateMessage("{}")] }),
                call =>
                {
                    receivedAgain.TrySetResult();

                    return WaitForReceiveCancellation(call.Arg<CancellationToken>());
                }
            );
        var logger = new RecordingLogger<NotificationCommandConsumer>();
        using var subject = CreateSubject(
            sqsClient,
            Substitute.For<INotificationDeliveryRecordStore>(),
            logger,
            pollIntervalSeconds: 2
        );

        var startedAt = Stopwatch.GetTimestamp();
        await subject.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForMessage("Notification command consumption failed", TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        Assert.False(receivedAgain.Task.IsCompleted);
        await receivedAgain.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(Stopwatch.GetElapsedTime(startedAt) >= TimeSpan.FromSeconds(1.8));
        await subject.StopAsync(TestContext.Current.CancellationToken);

        Assert.Single(logger.Messages);
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Levels));
        await sqsClient
            .DidNotReceive()
            .DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_WhenReceiveTimesOut_ShouldLogAndBackOffButShutdownCancellationShouldNotLog()
    {
        var receivedAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                async call =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());

                    return new ReceiveMessageResponse();
                },
                async call =>
                {
                    receivedAgain.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());

                    return new ReceiveMessageResponse();
                }
            );
        var logger = new RecordingLogger<NotificationCommandConsumer>();
        using var subject = CreateSubject(
            sqsClient,
            Substitute.For<INotificationDeliveryRecordStore>(),
            logger,
            pollIntervalSeconds: 2,
            receiveTimeoutSeconds: 1
        );

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForMessage("Notification command consumption failed", TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        Assert.False(receivedAgain.Task.IsCompleted);
        await receivedAgain.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        Assert.Single(logger.Messages);
        Assert.Empty(logger.Exceptions);
        Assert.Contains(
            logger.Messages,
            message => message.Contains("queue-error (TimeoutException)", StringComparison.Ordinal)
        );
    }

    private static async Task<ReceiveMessageResponse> WaitForReceiveCancellation(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ContinueWith(_ => { }, CancellationToken.None);

        return new ReceiveMessageResponse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_WhenApplicationHasNotStarted_ShouldWaitBeforeReceivingCommands(bool startApplication)
    {
        var readiness = new ApplicationStartup();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                received.TrySetResult();

                return Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>())
                    .ContinueWith(_ => new ReceiveMessageResponse(), CancellationToken.None);
            });
        using var subject = CreateSubject(
            sqsClient,
            Substitute.For<INotificationDeliveryRecordStore>(),
            readiness: readiness
        );

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        Assert.False(received.Task.IsCompleted);

        if (startApplication)
        {
            readiness.MarkStarted();
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        await subject.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(startApplication, received.Task.IsCompleted);
    }

    [Fact]
    public async Task Start_WhenPreCutoverCommandReceived_ShouldRecordSafeOutcomeBeforeDeletingMessage()
    {
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(MessageThenWait(CreateMessage(CommandBody("2026-09-28T10:00:00Z"))));
        sqsClient
            .DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>())
            .Returns(new DeleteMessageResponse())
            .AndDoes(_ => deleted.TrySetResult());
        var recordStore = Substitute.For<INotificationDeliveryRecordStore>();
        recordStore
            .RecordSuppression(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(SuppressionClaimResult.Recorded);
        var logger = new RecordingLogger<NotificationCommandConsumer>();
        var subject = CreateSubject(sqsClient, recordStore, logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        Received.InOrder(() =>
        {
            recordStore.RecordSuppression(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<CancellationToken>()
            );
            sqsClient.DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>());
        });
        Assert.Contains(logger.Messages, message => message.Contains("delivery-suppressed", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains(EmailAddress, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains(Personalisation, StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains(CommandIdempotencyKey, StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task Start_WhenCompressedPreCutoverCommandReceived_ShouldDeleteMessage()
    {
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(MessageThenWait(CreateMessage(Compress(CommandBody("2026-09-28T10:00:00Z")), "gzip+base64")));
        sqsClient
            .DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>())
            .Returns(new DeleteMessageResponse())
            .AndDoes(_ => deleted.TrySetResult());
        var recordStore = Substitute.For<INotificationDeliveryRecordStore>();
        recordStore
            .RecordSuppression(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(SuppressionClaimResult.Recorded);
        var subject = CreateSubject(sqsClient, recordStore);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        await sqsClient.Received(1).DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{}", null)]
    [InlineData("{ \"schemaVersion\": 1 }", null)]
    [InlineData("{ \"schemaVersion\": 1 }", "br")]
    public async Task Start_WhenCommandCannotBeRead_ShouldNotDeleteMessage(string body, string? contentEncoding)
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(MessageThenWait(CreateMessage(body, contentEncoding)));
        var logger = new RecordingLogger<NotificationCommandConsumer>();
        var subject = CreateSubject(sqsClient, Substitute.For<INotificationDeliveryRecordStore>(), logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForMessage("Notification command consumption failed", TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        await sqsClient
            .DidNotReceive()
            .DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_WhenContentEncodingHasNoStringValue_ShouldNotDeleteMessage()
    {
        var message = CreateMessage(CommandBody("2026-09-28T10:00:00Z"));
        message.MessageAttributes["Content-Encoding"] = new() { DataType = "String" };
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(MessageThenWait(message));
        var logger = new RecordingLogger<NotificationCommandConsumer>();
        var subject = CreateSubject(sqsClient, Substitute.For<INotificationDeliveryRecordStore>(), logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForMessage("Notification command consumption failed", TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        await sqsClient
            .DidNotReceive()
            .DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_WhenIdempotencyKeyConflicts_ShouldNotDeleteMessage()
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(MessageThenWait(CreateMessage(CommandBody("2026-09-28T10:00:00Z"))));
        var recordStore = Substitute.For<INotificationDeliveryRecordStore>();
        recordStore
            .RecordSuppression(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(SuppressionClaimResult.Conflict);
        var logger = new RecordingLogger<NotificationCommandConsumer>();
        var subject = CreateSubject(sqsClient, recordStore, logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForMessage("Notification command consumption failed", TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        await sqsClient
            .DidNotReceive()
            .DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_WhenSuppressedCommandIsDuplicated_ShouldDeleteMessageWithoutFurtherProcessing()
    {
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(MessageThenWait(CreateMessage(CommandBody("2026-09-28T10:00:00Z"))));
        sqsClient
            .DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>())
            .Returns(new DeleteMessageResponse())
            .AndDoes(_ => deleted.TrySetResult());
        var recordStore = Substitute.For<INotificationDeliveryRecordStore>();
        recordStore
            .RecordSuppression(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(SuppressionClaimResult.TerminalDuplicate);
        var logger = new RecordingLogger<NotificationCommandConsumer>();
        var subject = CreateSubject(sqsClient, recordStore, logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        Assert.Contains(
            logger.Messages,
            message =>
                message.Contains(
                    "terminal-duplicate (matching terminal evidence; no new request or suppression)",
                    StringComparison.Ordinal
                )
        );
        Assert.DoesNotContain(logger.Messages, message => message.Contains("would request", StringComparison.Ordinal));
        await sqsClient.Received(1).DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_WhenPostCutoverCommandCannotAcquireAClaim_ShouldNotDeleteMessage()
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(MessageThenWait(CreateMessage(CommandBody("2026-09-29T00:00:00Z"))));
        var logger = new RecordingLogger<NotificationCommandConsumer>();
        var subject = CreateSubject(sqsClient, Substitute.For<INotificationDeliveryRecordStore>(), logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForMessage("Notification command consumption failed", TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        await sqsClient
            .DidNotReceive()
            .DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stop_WhenDependencyThrowsPrivateFailureAfterCancellation_ShouldNotExposeItThroughHostLogging()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(MessageThenWait(CreateMessage(CommandBody("2026-09-28T10:00:00Z"))));
        var store = Substitute.For<INotificationDeliveryRecordStore>();
        store
            .RecordSuppression(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(FailAfterCancellation);

        async Task<SuppressionClaimResult> FailAfterCancellation(CallInfo call)
        {
            using var registration = call.Arg<CancellationToken>().Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await cancelled.Task;
            throw new InvalidOperationException(EmailAddress + Personalisation);
        }
        var logger = new RecordingLogger<NotificationCommandConsumer>();
        var subject = CreateSubject(sqsClient, store, logger);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().AddProvider(new RecordingLoggerProvider(logger));
        builder.Services.AddHostedService(_ => subject);
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        await subject
            .ExecuteTask!.ContinueWith(_ => { }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(subject.ExecuteTask.IsFaulted);
        Assert.Empty(logger.Exceptions);
        Assert.DoesNotContain(
            logger.Messages,
            message =>
                message.Contains(EmailAddress, StringComparison.Ordinal)
                || message.Contains(Personalisation, StringComparison.Ordinal)
        );
        await sqsClient
            .DidNotReceive()
            .DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_WhenCutoverMetricThrows_ShouldReportReceiveFailureWithoutProcessingMessage()
    {
        var sqs = Substitute.For<IAmazonSQS>();
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receive = MessageThenWait(CreateMessage(CommandBody("2026-09-28T10:00:00Z")));
        var receives = 0;
        sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (Interlocked.Increment(ref receives) == 2)
                    retried.TrySetResult();

                return receive(call);
            });
        var journeyMetrics = Substitute.For<INotificationJourneyMetrics>();
        journeyMetrics
            .When(metrics => metrics.RecordCutover(true))
            .Do(_ => throw new InvalidOperationException("private metric detail"));
        var store = Substitute.For<INotificationDeliveryRecordStore>();
        var logger = new RecordingLogger<NotificationCommandConsumer>();
        using var subject = CreateSubject(sqs, store, logger, journeyMetrics: journeyMetrics);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await retried.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            await subject.StopAsync(TestContext.Current.CancellationToken);
        }

        journeyMetrics
            .Received(1)
            .RecordFailure(NotificationJourneyValues.Consumer.Delivery, NotificationJourneyValues.Stage.Receive);
        Assert.Single(
            logger.Messages,
            message => message.Contains("Notification command consumption failed", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains("private metric detail", StringComparison.Ordinal)
        );
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Levels));
        Assert.Empty(store.ReceivedCalls());
        await sqs.DidNotReceive()
            .DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("duplicate")]
    [InlineData("suppressed")]
    public async Task WhenCorrelatedCommandIsHandled_ShouldTraceOutcomeAndFailureWithoutLeakingToNextMessage(
        string scenario
    )
    {
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        var capture = new TraceCapture();
        using var serilog = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(capture).CreateLogger();
        using var logs = new SerilogLoggerFactory(serilog, dispose: false);
        var sqs = Substitute.For<IAmazonSQS>();
        var first = CreateMessage(
            CommandBody("2026-10-01T10:00:00Z").TrimEnd()[..^1] + ",\"correlationId\":\"" + traceId + "\"}"
        );
        var receives = 0;
        sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var attempt = Interlocked.Increment(ref receives);
                if (attempt <= 2)
                    return new ReceiveMessageResponse
                    {
                        Messages = [attempt == 1 ? first : CreateMessage(CommandBody("2026-10-01T10:00:00Z"))],
                    };

                capture.Completed.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());

                return new ReceiveMessageResponse();
            });
        var deletes = 0;
        sqs.DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
                ++deletes == 1
                    ? Task.FromException<DeleteMessageResponse>(
                        new InvalidOperationException("private-recipient@example.invalid")
                    )
                    : Task.FromResult(new DeleteMessageResponse())
            );
        var store = Substitute.For<INotificationDeliveryRecordStore>();
        store
            .RecordSuppression(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(SuppressionClaimResult.Recorded);
        store
            .Claim(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(scenario == "duplicate" ? DeliveryClaimResult.TerminalDuplicate : DeliveryClaimResult.Claimed);
        store
            .RecordAcceptance(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<string>(),
                Arg.Any<NotifyAcceptance>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);
        var notify = Substitute.For<INotifyEmailClient>();
        notify
            .Send(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call => new NotifyAcceptance(
                "01234567-89ab-cdef-0123-456789abcdef",
                call.Arg<string>(),
                "template-1",
                1
            ));
        using var consumer = CreateSubject(
            sqs,
            store,
            logs.CreateLogger<NotificationCommandConsumer>(),
            cutover: scenario == "suppressed" ? null : "2026-09-29T00:00:00Z",
            notify: notify
        );

        await consumer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await capture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            await consumer.StopAsync(TestContext.Current.CancellationToken);
        }

        var outcomes = capture
            .Events.Where(entry =>
                entry.MessageTemplate.Text.StartsWith("Notification command outcome", StringComparison.Ordinal)
            )
            .ToArray();
        Assert.Equal(2, outcomes.Length);
        Assert.Equal(
            scenario switch
            {
                "accepted" => "delivery-accepted",
                "duplicate" => "terminal-duplicate",
                _ => "delivery-suppressed",
            },
            ((ScalarValue)outcomes[0].Properties["Outcome"]).Value
        );
        AssertEcsTrace(outcomes[0], traceId);
        AssertEcsTrace(Assert.Single(capture.Events, entry => entry.Level == LogEventLevel.Warning), traceId);
        AssertEcsTrace(outcomes[1], null);
        Assert.DoesNotContain(
            capture.Events,
            entry => entry.RenderMessage().Contains("private-recipient", StringComparison.Ordinal)
        );
    }

    private static void AssertEcsTrace(LogEvent entry, string? expected)
    {
        using var writer = new StringWriter();
        new EcsTextFormatter().Format(entry, writer);
        using var json = JsonDocument.Parse(writer.ToString());
        Assert.Equal(expected, json.RootElement.TryGetProperty("trace.id", out var trace) ? trace.GetString() : null);
    }

    private sealed class TraceCapture : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    private NotificationCommandConsumer CreateSubject(
        IAmazonSQS sqsClient,
        INotificationDeliveryRecordStore recordStore,
        ILogger<NotificationCommandConsumer>? logger = null,
        ApplicationStartup? readiness = null,
        int pollIntervalSeconds = 1,
        int receiveTimeoutSeconds = 30,
        string? cutover = "2026-09-29T00:00:00Z",
        INotifyEmailClient? notify = null,
        TimeProvider? timeProvider = null,
        INotificationJourneyMetrics? journeyMetrics = null
    ) =>
        new(
            sqsClient,
            Options.Create(
                new NotificationCommandDeliveryOptions
                {
                    QueueUrl = QueueUrl,
                    EmailDeliveryCutoverUtc = cutover,
                    EvidenceDigestSecret = "test-evidence-secret",
                    RecipientLaneSecret = "test-recipient-lane-secret",
                    WaitTimeSeconds = 0,
                    PollIntervalSeconds = pollIntervalSeconds,
                    ReceiveTimeoutSeconds = receiveTimeoutSeconds,
                }
            ),
            CreateRecordStoreFactory(recordStore),
            readiness ?? CompletedReadiness(),
            _metricServices.GetRequiredService<INotificationCommandMetrics>(),
            logger ?? new RecordingLogger<NotificationCommandConsumer>(),
            notify ?? Substitute.For<INotifyEmailClient>(),
            new NotificationCommandDigest(
                Options.Create(
                    new NotificationCommandDeliveryOptions
                    {
                        QueueUrl = QueueUrl,
                        EmailDeliveryCutoverUtc = cutover,
                        EvidenceDigestSecret = "test-evidence-secret",
                        RecipientLaneSecret = "test-lane-secret",
                    }
                )
            ),
            timeProvider ?? TimeProvider.System,
            journeyMetrics ?? _metricServices.GetRequiredService<INotificationJourneyMetrics>()
        );

    private static ApplicationStartup CompletedReadiness()
    {
        var readiness = new ApplicationStartup();
        readiness.MarkStarted();

        return readiness;
    }

    private static INotificationDeliveryRecordStoreFactory CreateRecordStoreFactory(
        INotificationDeliveryRecordStore recordStore
    )
    {
        var factory = Substitute.For<INotificationDeliveryRecordStoreFactory>();
        factory.GetRecordStore().Returns(recordStore);

        return factory;
    }

    private static string CommandBody(string actionOccurredAtUtc) =>
        $$"""
            {
              "schemaVersion": 1,
              "idempotencyKey": "{{CommandIdempotencyKey}}",
              "actionOccurredAtUtc": "{{actionOccurredAtUtc}}",
              "notificationType": "declaration-submitted",
              "emailAddress": "{{EmailAddress}}",
              "templateId": "template-1",
              "personalisation": { "body": "{{Personalisation}}" }
            }
            """;

    private static Func<CallInfo, Task<ReceiveMessageResponse>> MessageThenWait(Message message)
    {
        var receivedCount = 0;

        return call =>
        {
            if (Interlocked.Increment(ref receivedCount) == 1)
            {
                return Task.FromResult(new ReceiveMessageResponse { Messages = [message] });
            }

            var cancellationToken = call.ArgAt<CancellationToken>(1);

            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ContinueWith(_ => new ReceiveMessageResponse(), CancellationToken.None);
        };
    }

    private static Message CreateMessage(string body, string? contentEncoding = null)
    {
        var message = new Message
        {
            Body = body,
            MessageAttributes = [],
            MessageId = "message-id-1",
            ReceiptHandle = ReceiptHandle,
        };

        if (contentEncoding is not null)
        {
            message.MessageAttributes["Content-Encoding"] = new()
            {
                DataType = "String",
                StringValue = contentEncoding,
            };
        }

        return message;
    }

    private static string Compress(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        using var output = new MemoryStream();

        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
        {
            gzip.Write(bytes);
        }

        return Convert.ToBase64String(output.ToArray());
    }

    private sealed class RecordingLoggerProvider(ILogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose() { }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];
        private readonly List<LogLevel> _levels = [];
        private readonly List<Exception> _exceptions = [];

        public IReadOnlyList<string> Messages => _messages;
        public IReadOnlyList<LogLevel> Levels => _levels;
        public IReadOnlyList<Exception> Exceptions => _exceptions;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (_messages)
            {
                _messages.Add(formatter(state, exception));
                _levels.Add(logLevel);
                if (exception is not null)
                    _exceptions.Add(exception);
            }
        }

        public async Task WaitForMessage(string expected, CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_messages)
                {
                    if (_messages.Any(message => message.StartsWith(expected, StringComparison.Ordinal)))
                    {
                        return;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }
    }
}
