using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests.Delivery;

public sealed class NotificationCommandSendTests
{
    private const string QueueUrl = "commands.fifo";
    private const string Reference = "v1:82334b5671ae90cb56279cc2a9924a629589f6b31ef04a870af3c0a01a2fa095";
    private const string PrivateValues = "private-key recipient@example.com private-body template-1";

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
            }
        );

        await consumer.StartAsync(TestContext.Current.CancellationToken);
        await sending.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        if (!shutdown)
            await logger.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await consumer.StopAsync(TestContext.Current.CancellationToken);

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

    private static async Task RunUntilFailure(NotificationCommandConsumer consumer, RecordingLogger logger)
    {
        await consumer.StartAsync(TestContext.Current.CancellationToken);
        await logger.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await consumer.StopAsync(TestContext.Current.CancellationToken);
    }

    private static NotificationCommandConsumer Consumer(
        IAmazonSQS sqs,
        INotificationDeliveryRecordStore store,
        INotifyEmailClient notify,
        RecordingLogger logger,
        NotificationCommandDeliveryOptions? options = null
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
                    }
            ),
            factory,
            readiness,
            new NotificationCommandMetrics(),
            logger,
            notify,
            digest
        );
    }

    private static IAmazonSQS Sqs()
    {
        var sqs = Substitute.For<IAmazonSQS>();
        sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromResult(new ReceiveMessageResponse { Messages = [Message()] }),
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

    private static Message Message() =>
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
                    notificationType = "submitted",
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
