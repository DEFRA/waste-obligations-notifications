using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Utils.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests.Delivery;

public sealed class NotificationCommandSendTests : IDisposable
{
    private const string QueueUrl = "commands.fifo";
    private const string Reference = "v1:82334b5671ae90cb56279cc2a9924a629589f6b31ef04a870af3c0a01a2fa095";
    private const string PrivateValues = "private-key recipient@example.com private-body template-1";
    private readonly ServiceProvider _metricServices = new ServiceCollection()
        .AddNotificationCommandMetrics()
        .BuildServiceProvider();

    public void Dispose() => _metricServices.Dispose();

    [Fact]
    public async Task WhenClaimAndSendSucceed_ShouldRecordAcceptanceBeforeDeletingAndExcludePrivateDataFromLogs()
    {
        var sqs = Sqs();
        var store = Store();
        var notify = Notify();
        var logger = new RecordingLogger();
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sqs.DeleteMessageAsync(QueueUrl, "receipt", Arg.Any<CancellationToken>())
            .Returns(new DeleteMessageResponse())
            .AndDoes(_ => deleted.TrySetResult());
        using var consumer = Consumer(sqs, store, notify, logger);

        await consumer.StartAsync(TestContext.Current.CancellationToken);
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await consumer.StopAsync(TestContext.Current.CancellationToken);

        Received.InOrder(() =>
        {
            store.Claim(Arg.Any<NotificationCommand>(), Arg.Any<string>(), 120, Arg.Any<CancellationToken>());
            notify.Send(
                Arg.Is<NotificationCommand>(command => command.EmailAddress == "recipient@example.com"),
                Reference,
                Arg.Any<CancellationToken>()
            );
            store.RecordAcceptance(
                Arg.Any<NotificationCommand>(),
                Arg.Any<string>(),
                Arg.Any<NotifyAcceptance>(),
                Arg.Any<CancellationToken>()
            );
            sqs.DeleteMessageAsync(QueueUrl, "receipt", Arg.Any<CancellationToken>());
        });
        foreach (var value in PrivateValues.Split(' '))
            Assert.DoesNotContain(value, string.Join('\n', logger.Messages), StringComparison.Ordinal);
        await sqs.Received()
            .ReceiveMessageAsync(
                Arg.Is<ReceiveMessageRequest>(request => request.VisibilityTimeout == 120),
                Arg.Any<CancellationToken>()
            );
        var claimOwner = store.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "Claim").GetArguments()[1];
        var acceptanceOwner = store
            .ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == "RecordAcceptance")
            .GetArguments()[1];
        Assert.Equal(claimOwner, acceptanceOwner);
        Assert.True(Guid.TryParse((string)claimOwner!, out _));
    }

    [Theory]
    [InlineData(DeliveryClaimResult.ActiveClaim)]
    [InlineData(DeliveryClaimResult.Conflict)]
    [InlineData(DeliveryClaimResult.Unavailable)]
    public async Task WhenClaimIsUnavailable_ShouldNeitherSendNorDelete(DeliveryClaimResult claim)
    {
        var sqs = Sqs();
        var store = Store();
        store
            .Claim(Arg.Any<NotificationCommand>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(claim);
        var notify = Notify();
        var logger = new RecordingLogger();
        using var consumer = Consumer(sqs, store, notify, logger);

        await RunUntilFailure(consumer, logger);

        await notify
            .DidNotReceive()
            .Send(Arg.Any<NotificationCommand>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await sqs.DidNotReceive()
            .DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("send")]
    [InlineData("persistence")]
    [InlineData("ownership")]
    public async Task WhenSendOrAcceptanceFails_ShouldLeaveMessageAndSanitizeDependencyErrors(string failure)
    {
        var sqs = Sqs();
        var store = Store();
        var notify = Notify();
        if (failure == "send")
            notify
                .Send(Arg.Any<NotificationCommand>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<NotifyAcceptance>(new InvalidOperationException(PrivateValues)));
        if (failure == "persistence")
            store
                .RecordAcceptance(
                    Arg.Any<NotificationCommand>(),
                    Arg.Any<string>(),
                    Arg.Any<NotifyAcceptance>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(Task.FromException<bool>(new InvalidOperationException(PrivateValues)));
        if (failure == "ownership")
            store
                .RecordAcceptance(
                    Arg.Any<NotificationCommand>(),
                    Arg.Any<string>(),
                    Arg.Any<NotifyAcceptance>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(false);
        var logger = new RecordingLogger();
        using var consumer = Consumer(sqs, store, notify, logger);

        await RunUntilFailure(consumer, logger);

        await sqs.DidNotReceive()
            .DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(PrivateValues, string.Join('\n', logger.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain(
            logger.Exceptions,
            exception => exception.ToString().Contains(PrivateValues, StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task WhenDeleteFailsAfterAcceptance_ShouldDeleteTerminalRedeliveryWithoutSendingAgain()
    {
        var sqs = Sqs();
        sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromResult(new ReceiveMessageResponse { Messages = [Message()] }),
                _ => Task.FromResult(new ReceiveMessageResponse { Messages = [Message()] }),
                call => Wait(call.Arg<CancellationToken>())
            );
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sqs.DeleteMessageAsync(QueueUrl, "receipt", Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException<DeleteMessageResponse>(new InvalidOperationException("Delete failed")),
                _ =>
                {
                    deleted.TrySetResult();
                    return Task.FromResult(new DeleteMessageResponse());
                }
            );
        var store = Store();
        store
            .Claim(Arg.Any<NotificationCommand>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(DeliveryClaimResult.Claimed, DeliveryClaimResult.TerminalDuplicate);
        var notify = Notify();
        using var consumer = Consumer(sqs, store, notify, new RecordingLogger());

        await consumer.StartAsync(TestContext.Current.CancellationToken);
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await consumer.StopAsync(TestContext.Current.CancellationToken);

        await notify.Received(1).Send(Arg.Any<NotificationCommand>(), Reference, Arg.Any<CancellationToken>());
        await store
            .Received(1)
            .RecordAcceptance(
                Arg.Any<NotificationCommand>(),
                Arg.Any<string>(),
                Arg.Any<NotifyAcceptance>(),
                Arg.Any<CancellationToken>()
            );
        var owners = store
            .ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == "Claim")
            .Select(call => call.GetArguments()[1])
            .ToArray();
        Assert.NotEqual(owners[0], owners[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenNotifyTimesOutOrHostStops_ShouldCancelActualRequestAndNeverDelete(bool shutdown)
    {
        using var measurements = new RecordingMeasurements();
        var sqs = Sqs();
        var store = Store();
        var notify = Notify();
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        notify
            .Send(Arg.Any<NotificationCommand>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                sending.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    throw;
                }

                return Acceptance();
            });
        var logger = new RecordingLogger();
        using var consumer = Consumer(
            sqs,
            store,
            notify,
            logger,
            new()
            {
                QueueUrl = QueueUrl,
                ProcessingEnabled = true,
                EmailDeliveryCutoverUtc = "2026-09-29T00:00:00Z",
                EvidenceDigestSecret = "test-secret",
                RecipientLaneSecret = "test-lane",
                NotifyTimeoutSeconds = 1,
            },
            measurements.Instrumentation
        );

        await consumer.StartAsync(TestContext.Current.CancellationToken);
        await sending.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        if (!shutdown)
            await logger.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await consumer.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            1,
            Assert
                .Single(measurements.Events, measurement => measurement.Name == "NotificationCommandNotifySendFailure")
                .Value
        );
        Assert.DoesNotContain(
            measurements.Events,
            measurement => measurement.Name == "NotificationCommandNotifySendAccepted"
        );
        var duration = Assert.Single(
            measurements.Events,
            measurement => measurement.Name == "NotificationCommandNotifySendDuration"
        );
        Assert.Equal("MILLISECONDS", duration.Unit);
        Assert.True(duration.Value > 0);
        if (!shutdown)
            Assert.True(duration.Value >= 900);
        Assert.True(cancelled);
        Assert.Equal(!shutdown, logger.Failed.Task.IsCompleted);
        await store
            .DidNotReceive()
            .RecordAcceptance(
                Arg.Any<NotificationCommand>(),
                Arg.Any<string>(),
                Arg.Any<NotifyAcceptance>(),
                Arg.Any<CancellationToken>()
            );
        await sqs.DidNotReceive()
            .DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenReceiveOrClaimConfirmationArrivesAfterDeadline_ShouldNotSend(bool receive)
    {
        var sqs = Sqs();
        var store = Store();
        if (receive)
            sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    await Task.Delay(1200, TestContext.Current.CancellationToken);
                    return new ReceiveMessageResponse { Messages = [Message()] };
                });
        else
            store
                .Claim(Arg.Any<NotificationCommand>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    await Task.Delay(1200, TestContext.Current.CancellationToken);
                    return DeliveryClaimResult.Claimed;
                });
        var notify = Notify();
        var logger = new RecordingLogger();
        using var consumer = Consumer(
            sqs,
            store,
            notify,
            logger,
            new()
            {
                QueueUrl = QueueUrl,
                ProcessingEnabled = true,
                EmailDeliveryCutoverUtc = "2026-09-29T00:00:00Z",
                EvidenceDigestSecret = "test-secret",
                RecipientLaneSecret = "test-lane",
                ReceiveTimeoutSeconds = 1,
                WaitTimeSeconds = 0,
                ClaimTimeoutSeconds = 1,
            }
        );

        await RunUntilFailure(consumer, logger);

        await notify
            .DidNotReceive()
            .Send(Arg.Any<NotificationCommand>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await sqs.DidNotReceive()
            .DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenAcceptanceConfirmationArrivesAfterItsDeadline_ShouldLeaveTheMessageUndeleted()
    {
        var sqs = Sqs();
        var store = Store();
        store
            .RecordAcceptance(
                Arg.Any<NotificationCommand>(),
                Arg.Any<string>(),
                Arg.Any<NotifyAcceptance>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(async _ =>
            {
                await Task.Delay(1200, TestContext.Current.CancellationToken);
                return true;
            });
        var notify = Notify();
        var logger = new RecordingLogger();
        using var consumer = Consumer(
            sqs,
            store,
            notify,
            logger,
            new()
            {
                QueueUrl = QueueUrl,
                ProcessingEnabled = true,
                EmailDeliveryCutoverUtc = "2026-09-29T00:00:00Z",
                EvidenceDigestSecret = "test-secret",
                RecipientLaneSecret = "test-lane",
                AcceptanceTimeoutSeconds = 1,
            }
        );

        await RunUntilFailure(consumer, logger);

        await notify.Received(1).Send(Arg.Any<NotificationCommand>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await sqs.DidNotReceive()
            .DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("accepted", "claimed", 1, 0, 0)]
    [InlineData("send-failed", "claimed", 0, 1, 0)]
    [InlineData("acceptance-failed", "claimed", 1, 0, 0)]
    [InlineData("active-claim", "active-claim", 0, 0, 0)]
    [InlineData("terminal-duplicate", "terminal-duplicate", 0, 0, 1)]
    [InlineData("claim-failed", "failure", 0, 0, 0)]
    [InlineData("suppressed", null, 0, 0, 0)]
    [InlineData("suppressed-duplicate", null, 0, 0, 1)]
    public async Task WhenCommandProcessingProducesAnOutcome_ShouldPublishOnlyItsObservableDeliveryMetrics(
        string condition,
        string? claimOutcome,
        int acceptedSends,
        int failedSends,
        int duplicates
    )
    {
        using var measurements = new RecordingMeasurements();
        var sqs = Sqs("metrics-test", condition.StartsWith("suppressed", StringComparison.Ordinal));
        var store = Store();
        var notify = Notify();
        if (condition == "send-failed")
            notify
                .Send(Arg.Any<NotificationCommand>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<NotifyAcceptance>(new InvalidOperationException(PrivateValues)));
        if (condition == "acceptance-failed")
            store
                .RecordAcceptance(
                    Arg.Any<NotificationCommand>(),
                    Arg.Any<string>(),
                    Arg.Any<NotifyAcceptance>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(false);
        if (condition == "active-claim")
            store
                .Claim(Arg.Any<NotificationCommand>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(DeliveryClaimResult.ActiveClaim);
        if (condition == "terminal-duplicate")
            store
                .Claim(Arg.Any<NotificationCommand>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(DeliveryClaimResult.TerminalDuplicate);
        if (condition == "claim-failed")
            store
                .Claim(Arg.Any<NotificationCommand>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<DeliveryClaimResult>(new InvalidOperationException(PrivateValues)));
        if (condition == "suppressed-duplicate")
            store
                .RecordSuppression(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
                .Returns(SuppressionClaimResult.TerminalDuplicate);
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sqs.DeleteMessageAsync(QueueUrl, "receipt", Arg.Any<CancellationToken>())
            .Returns(new DeleteMessageResponse())
            .AndDoes(_ => deleted.TrySetResult());
        var logger = new RecordingLogger();
        using var consumer = Consumer(sqs, store, notify, logger, metrics: measurements.Instrumentation);

        await consumer.StartAsync(TestContext.Current.CancellationToken);
        await (
            condition is "send-failed" or "acceptance-failed" or "active-claim" or "claim-failed"
                ? logger.Failed.Task
                : deleted.Task
        ).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await consumer.StopAsync(TestContext.Current.CancellationToken);

        var events = measurements.Events;
        Assert.Equal(
            1,
            events
                .Where(measurement => measurement.Name == "NotificationCommandReceived")
                .Sum(measurement => measurement.Value)
        );
        Assert.Equal(
            acceptedSends,
            events
                .Where(measurement => measurement.Name == "NotificationCommandNotifySendAccepted")
                .Sum(measurement => measurement.Value)
        );
        Assert.Equal(
            failedSends,
            events
                .Where(measurement => measurement.Name == "NotificationCommandNotifySendFailure")
                .Sum(measurement => measurement.Value)
        );
        Assert.Equal(
            duplicates,
            events
                .Where(measurement => measurement.Name == "NotificationCommandDuplicateSuppressed")
                .Sum(measurement => measurement.Value)
        );
        var claims = events.Where(measurement => measurement.Name == "NotificationCommandLeaseClaim").ToArray();
        if (claimOutcome is null)
            Assert.Empty(claims);
        else
            Assert.Equal(claimOutcome, Assert.Single(claims).Tags.Single(tag => tag.Key == "Outcome").Value);
        var claimDurations = events
            .Where(measurement => measurement.Name == "NotificationCommandLeaseClaimDuration")
            .ToArray();
        Assert.Equal(claimOutcome is null ? 0 : 1, claimDurations.Length);
        var sendDurations = events
            .Where(measurement => measurement.Name == "NotificationCommandNotifySendDuration")
            .ToArray();
        Assert.Equal(acceptedSends + failedSends, sendDurations.Length);
        Assert.All(
            claimDurations.Concat(sendDurations),
            measurement =>
            {
                Assert.Equal("MILLISECONDS", measurement.Unit);
                Assert.True(measurement.Value >= 0);
            }
        );
        Assert.All(
            events.Where(measurement => !measurement.Name.EndsWith("Duration", StringComparison.Ordinal)),
            measurement => Assert.Equal("COUNT", measurement.Unit)
        );
        Assert.All(
            events,
            measurement =>
                Assert.All(
                    measurement.Tags,
                    tag => Assert.True(tag.Key is "Service" or "NotificationType" or "Outcome")
                )
        );
        foreach (var value in PrivateValues.Split(' '))
            Assert.DoesNotContain(
                value,
                string.Join(' ', events.SelectMany(measurement => measurement.Tags).Select(tag => tag.Value)),
                StringComparison.Ordinal
            );
        Assert.All(
            events,
            measurement =>
                Assert.Equal(
                    "waste-obligations-notifications",
                    Assert.Single(measurement.Tags, tag => tag.Key == "Service").Value
                )
        );
        Assert.All(
            events.SelectMany(measurement => measurement.Tags).Where(tag => tag.Key == "NotificationType"),
            tag => Assert.Equal("metrics-test", tag.Value)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenNotificationTypeContainsPrivateText_ShouldKeepCommandIdentityAndUseFallbackDiagnostics(
        bool sendFails
    )
    {
        using var measurements = new RecordingMeasurements();
        var sqs = Sqs(PrivateValues);
        var store = Store();
        var notify = Notify();
        if (sendFails)
            notify
                .Send(Arg.Any<NotificationCommand>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<NotifyAcceptance>(new InvalidOperationException(PrivateValues)));
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sqs.DeleteMessageAsync(QueueUrl, "receipt", Arg.Any<CancellationToken>())
            .Returns(new DeleteMessageResponse())
            .AndDoes(_ => deleted.TrySetResult());
        var logger = new RecordingLogger();
        using var consumer = Consumer(sqs, store, notify, logger, metrics: measurements.Instrumentation);

        await consumer.StartAsync(TestContext.Current.CancellationToken);
        await (sendFails ? logger.Failed.Task : deleted.Task).WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken
        );
        await consumer.StopAsync(TestContext.Current.CancellationToken);

        await store
            .Received(1)
            .Claim(
                Arg.Is<NotificationCommand>(command => command.NotificationType == PrivateValues),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>()
            );
        Assert.Contains(
            logger.Messages,
            message => message.Contains("for other from SQS message", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(PrivateValues, string.Join(' ', logger.Messages), StringComparison.Ordinal);
        Assert.Single(measurements.Events, measurement => measurement.Name == "NotificationCommandReceived");
        Assert.Single(measurements.Events, measurement => measurement.Name == "NotificationCommandLeaseClaim");
        Assert.Single(measurements.Events, measurement => measurement.Name == "NotificationCommandLeaseClaimDuration");
        Assert.Single(
            measurements.Events,
            measurement =>
                measurement.Name
                == (sendFails ? "NotificationCommandNotifySendFailure" : "NotificationCommandNotifySendAccepted")
        );
        Assert.Single(measurements.Events, measurement => measurement.Name == "NotificationCommandNotifySendDuration");
        Assert.All(
            measurements.Events.SelectMany(measurement => measurement.Tags).Where(tag => tag.Key == "NotificationType"),
            tag => Assert.Equal("other", tag.Value)
        );
        foreach (var value in PrivateValues.Split(' '))
            Assert.DoesNotContain(
                value,
                string.Join(
                    ' ',
                    measurements.Events.SelectMany(measurement => measurement.Tags).Select(tag => tag.Value)
                ),
                StringComparison.Ordinal
            );
    }

    [Fact]
    public void WhenMetricsHostsAreDisposedIndependently_ShouldKeepMeasurementsWithinTheirOwningHost()
    {
        using var first = new RecordingMeasurements();
        using var second = new RecordingMeasurements();
        Assert.Same(first.Instrumentation, first.ResolveInstrumentation());
        Assert.Same(second.Instrumentation, second.ResolveInstrumentation());

        first.Instrumentation.RecordReceived("metrics-test");
        Assert.Single(first.Events);
        Assert.Empty(second.Events);

        first.DisposeHost();
        first.Instrumentation.RecordSendFailure("metrics-test");
        second.Instrumentation.RecordReceived("metrics-test");
        second.Instrumentation.RecordSendFailure("metrics-test");

        Assert.Single(first.Events);
        Assert.Equal(
            ["NotificationCommandReceived", "NotificationCommandNotifySendFailure"],
            second.Events.Select(measurement => measurement.Name)
        );
    }

    private sealed class RecordingMeasurements : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<Measurement> _events = new();
        private readonly ServiceProvider _services = new ServiceCollection()
            .AddNotificationCommandMetrics()
            .BuildServiceProvider();
        public INotificationCommandMetrics Instrumentation { get; }
        public Measurement[] Events => _events.ToArray();

        public RecordingMeasurements()
        {
            var meter = _services.GetRequiredService<IMeterFactory>().Create(Metrics.MeterName);
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>(
                (instrument, value, tags, _) => Record(instrument, value, tags)
            );
            _listener.SetMeasurementEventCallback<double>(
                (instrument, value, tags, _) => Record(instrument, value, tags)
            );
            _listener.Start();
            Instrumentation = _services.GetRequiredService<INotificationCommandMetrics>();
        }

        public INotificationCommandMetrics ResolveInstrumentation() =>
            _services.GetRequiredService<INotificationCommandMetrics>();

        public void DisposeHost() => _services.Dispose();

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            _events.Enqueue(new(instrument.Name, value, instrument.Unit, tags.ToArray()));
        }

        public void Dispose()
        {
            _listener.Dispose();
            _services.Dispose();
        }
    }

    private sealed record Measurement(string Name, double Value, string? Unit, KeyValuePair<string, object?>[] Tags);

    private static async Task RunUntilFailure(NotificationCommandConsumer consumer, RecordingLogger logger)
    {
        await consumer.StartAsync(TestContext.Current.CancellationToken);
        await logger.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await consumer.StopAsync(TestContext.Current.CancellationToken);
    }

    private NotificationCommandConsumer Consumer(
        IAmazonSQS sqs,
        INotificationDeliveryRecordStore store,
        INotifyEmailClient notify,
        RecordingLogger logger,
        NotificationCommandDeliveryOptions? options = null,
        INotificationCommandMetrics? metrics = null
    )
    {
        var readiness = new MongoMigrationReadiness();
        readiness.MarkCompleted();
        var factory = Substitute.For<INotificationDeliveryRecordStoreFactory>();
        factory.GetRecordStore().Returns(store);
        var digest = new NotificationCommandDigest(
            Options.Create(
                new NotificationCommandDeliveryOptions
                {
                    QueueUrl = QueueUrl,
                    EmailDeliveryCutoverUtc = "2026-09-29T00:00:00Z",
                    EvidenceDigestSecret = "test-secret",
                    RecipientLaneSecret = "test-lane",
                }
            )
        );

        return new(
            sqs,
            Options.Create(
                options
                    ?? new()
                    {
                        QueueUrl = QueueUrl,
                        ProcessingEnabled = true,
                        EmailDeliveryCutoverUtc = "2026-09-29T00:00:00Z",
                        EvidenceDigestSecret = "test-secret",
                        RecipientLaneSecret = "test-lane",
                        PollIntervalSeconds = 1,
                        DiagnosticNotificationTypes = ["metrics-test"],
                    }
            ),
            factory,
            readiness,
            metrics ?? _metricServices.GetRequiredService<INotificationCommandMetrics>(),
            logger,
            notify,
            digest
        );
    }

    private static IAmazonSQS Sqs(string notificationType = "submitted", bool beforeCutover = false)
    {
        var sqs = Substitute.For<IAmazonSQS>();
        sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ =>
                    Task.FromResult(
                        new ReceiveMessageResponse
                        {
                            Messages =
                            [
                                Message(
                                    notificationType,
                                    beforeCutover ? "2026-09-28T00:00:00Z" : "2026-09-29T00:00:00Z"
                                ),
                            ],
                        }
                    ),
                call => Wait(call.Arg<CancellationToken>())
            );

        return sqs;
    }

    private static INotificationDeliveryRecordStore Store()
    {
        var store = Substitute.For<INotificationDeliveryRecordStore>();
        store
            .Claim(Arg.Any<NotificationCommand>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(DeliveryClaimResult.Claimed);
        store
            .RecordAcceptance(
                Arg.Any<NotificationCommand>(),
                Arg.Any<string>(),
                Arg.Any<NotifyAcceptance>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);

        return store;
    }

    private static INotifyEmailClient Notify()
    {
        var notify = Substitute.For<INotifyEmailClient>();
        notify
            .Send(Arg.Any<NotificationCommand>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Acceptance());

        return notify;
    }

    private static NotifyAcceptance Acceptance() =>
        new("01234567-89ab-cdef-0123-456789abcdef", Reference, "template-1", 1);

    private static Message Message(string notificationType = "submitted", string timestamp = "2026-09-29T00:00:00Z") =>
        new()
        {
            MessageId = "message-1",
            ReceiptHandle = "receipt",
            Body = JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    idempotencyKey = "private-key",
                    actionOccurredAtUtc = timestamp,
                    notificationType,
                    emailAddress = " Recipient@Example.com ",
                    templateId = "template-1",
                    personalisation = new { body = "private-body" },
                }
            ),
        };

    private static async Task<ReceiveMessageResponse> Wait(CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);

        return new();
    }

    private sealed class RecordingLogger : ILogger<NotificationCommandConsumer>
    {
        public List<string> Messages { get; } = [];
        public List<Exception> Exceptions { get; } = [];
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            Messages.Add(formatter(state, exception));
            if (exception is not null)
                Exceptions.Add(exception);
            if (logLevel == LogLevel.Error)
                Failed.TrySetResult();
        }
    }
}
