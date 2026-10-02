using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Startup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.Core;

namespace Defra.WasteObligations.Consumer.Tests.Delivery;

public class NotificationCommandConsumerTests
{
    private const string CommandIdempotencyKey = "command-key-1";
    private const string EmailAddress = "recipient@example.com";
    private const string Personalisation = "secret personalisation";
    private const string QueueUrl = "http://localhost:4566/000000000000/commands.fifo";
    private const string ReceiptHandle = "receipt-handle-1";

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
        using var subject = CreateSubject(sqsClient, recordStore, logger, cutover: cutover);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        if (suppressed)
            await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        else
            await logger.WaitForMessage(
                "Notification command consumption failed",
                TestContext.Current.CancellationToken
            );
        await subject.StopAsync(TestContext.Current.CancellationToken);

        await recordStore
            .Received(suppressed ? 1 : 0)
            .RecordSuppression(
                Arg.Any<global::Defra.WasteObligations.Consumer.Commands.NotificationCommand>(),
                Arg.Any<CancellationToken>()
            );
        await sqsClient
            .Received(suppressed ? 1 : 0)
            .DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>());
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
    public async Task Start_WhenReceiveOrProcessingFails_ShouldBackOffBeforeReceivingAgain(bool receiveFails)
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
        Assert.IsType<TimeoutException>(Assert.Single(logger.Exceptions));
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
        var subject = CreateSubject(sqsClient, recordStore);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        await sqsClient.Received(1).DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_WhenCommandIsAtOrAfterCutover_ShouldNotDeleteMessage()
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

    private static NotificationCommandConsumer CreateSubject(
        IAmazonSQS sqsClient,
        INotificationDeliveryRecordStore recordStore,
        ILogger<NotificationCommandConsumer>? logger = null,
        ApplicationStartup? readiness = null,
        int pollIntervalSeconds = 1,
        int receiveTimeoutSeconds = 30,
        string? cutover = "2026-09-29T00:00:00Z"
    ) =>
        new(
            sqsClient,
            Options.Create(
                new NotificationCommandDeliveryOptions
                {
                    QueueUrl = QueueUrl,
                    ProcessingEnabled = true,
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
            new NotificationCommandMetrics(),
            logger ?? new RecordingLogger<NotificationCommandConsumer>()
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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];
        private readonly List<Exception> _exceptions = [];

        public IReadOnlyList<string> Messages => _messages;
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
                    if (_messages.Contains(expected))
                    {
                        return;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }
    }
}
