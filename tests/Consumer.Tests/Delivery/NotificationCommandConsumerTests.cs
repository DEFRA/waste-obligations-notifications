using System.IO.Compression;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Delivery;
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
        ILogger<NotificationCommandConsumer>? logger = null
    ) =>
        new(
            sqsClient,
            Options.Create(
                new NotificationCommandDeliveryOptions
                {
                    QueueUrl = QueueUrl,
                    ProcessingEnabled = true,
                    EmailDeliveryCutoverUtc = "2026-09-29T00:00:00Z",
                    EvidenceDigestSecret = "test-evidence-secret",
                    RecipientLaneSecret = "test-recipient-lane-secret",
                    MongoConnectionString = "mongodb://localhost:27017",
                    MongoDatabaseName = "notifications",
                    WaitTimeSeconds = 0,
                    PollIntervalSeconds = 1,
                }
            ),
            CreateRecordStoreFactory(recordStore),
            new NotificationCommandMetrics(),
            logger ?? new RecordingLogger<NotificationCommandConsumer>()
        );

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

        public IReadOnlyList<string> Messages => _messages;

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
