using System.IO.Compression;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Cancellation;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Notifications;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
using Defra.WasteObligations.Consumer.Services.WasteOrganisations;
using Defra.WasteObligations.Consumer.Startup;
using Defra.WasteObligations.Consumer.Submission;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.Core;

namespace Defra.WasteObligations.Consumer.Tests.Consumers;

public class AnalyticsEventConsumerTests
{
    private const string EntityId = "cdec_65f1f6570bb08052a8a27b01";
    private const string EventId = "01JZ8RXBMTY2K15SJB3PCFN3D5";
    private const string QueueUrl =
        "http://localhost:4566/000000000000/waste_obligations_notifications_analytics_events_queue";
    private const string ReceiptHandle = "receipt-handle-1";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_WhenReceiveSucceedsOrIsEmpty_ShouldImmediatelyReceiveAgain(bool hasMessage)
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
                        new ReceiveMessageResponse { Messages = hasMessage ? [CreateMessage(Body())] : [] }
                    ),
                call =>
                {
                    Assert.Equal(1, call.Arg<ReceiveMessageRequest>().MaxNumberOfMessages);
                    Assert.Equal(hasMessage, deleted);
                    receivedAgain.TrySetResult();

                    return WaitForCancellation(call.Arg<CancellationToken>());
                }
            );
        using var subject = CreateSubject(sqsClient, pollIntervalSeconds: 300);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await receivedAgain.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        await sqsClient.Received(2).ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_WhenDependencyFails_ShouldBackOffThenHandleNextMessage(bool timeout)
    {
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sqsClient = Substitute.For<IAmazonSQS>();
        var receives = 0;
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var count = Interlocked.Increment(ref receives);
                if (count == 1)
                    return Task.FromException<ReceiveMessageResponse>(
                        timeout
                            ? new OperationCanceledException("unsafe dependency detail")
                            : new InvalidOperationException("unsafe dependency detail")
                    );
                if (count == 2)
                {
                    Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(startedAt) >= TimeSpan.FromSeconds(0.9));

                    return Task.FromResult(new ReceiveMessageResponse { Messages = [CreateMessage(Body())] });
                }

                return WaitForCancellation(call.Arg<CancellationToken>());
            });
        sqsClient
            .DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>())
            .Returns(new DeleteMessageResponse())
            .AndDoes(_ => deleted.TrySetResult());
        var logger = new RecordingLogger<AnalyticsEventConsumer>();
        using var subject = CreateSubject(sqsClient, logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForMessage("Analytics event consumption failed", TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref receives));
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logger.Messages, message => message.Contains("unsafe dependency detail"));
        Assert.Empty(logger.Exceptions);
        await sqsClient.Received(1).DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>());
    }

    private static async Task<ReceiveMessageResponse> WaitForCancellation(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        return new ReceiveMessageResponse();
    }

    [Fact]
    public async Task Start_WhenProcessingIsDisabled_ShouldNotReceiveMessages()
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        var subject = CreateSubject(sqsClient, processingEnabled: false);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        await sqsClient
            .DidNotReceive()
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_WhenValidMessageReceived_ShouldLogAndDeleteMessage()
    {
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(MessageThenWait(CreateMessage(Body())));
        sqsClient
            .DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>())
            .Returns(new DeleteMessageResponse())
            .AndDoes(_ => deleted.TrySetResult());
        var logger = new RecordingLogger<AnalyticsEventConsumer>();
        var subject = CreateSubject(sqsClient, logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForMessage("Consumed analytics event", TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        Assert.Contains(logger.Messages, message => message.Contains(EventId) && message.Contains(EntityId));
        await sqsClient.Received(1).DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_WhenCompressedMessageReceived_ShouldLogAndDeleteMessage()
    {
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(MessageThenWait(CreateMessage(Compress(Body()), "gzip+base64")));
        sqsClient
            .DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>())
            .Returns(new DeleteMessageResponse())
            .AndDoes(_ => deleted.TrySetResult());
        var logger = new RecordingLogger<AnalyticsEventConsumer>();
        var subject = CreateSubject(sqsClient, logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForMessage("Consumed analytics event", TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        Assert.Contains(logger.Messages, message => message.Contains(EventId) && message.Contains(EntityId));
        await sqsClient.Received(1).DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_WhenMessageIsMalformed_ShouldNotDeleteMessage()
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(MessageThenWait(CreateMessage("{}")));
        var logger = new RecordingLogger<AnalyticsEventConsumer>();
        var subject = CreateSubject(sqsClient, logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForMessage("Analytics event consumption failed", TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        await sqsClient
            .DidNotReceive()
            .DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_WhenMessageContentEncodingIsUnsupported_ShouldLogErrorAndNotDeleteMessage()
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(MessageThenWait(CreateMessage(Body(), "br")));
        var logger = new RecordingLogger<AnalyticsEventConsumer>();
        var subject = CreateSubject(sqsClient, logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForMessage("Analytics event consumption failed", TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        await sqsClient
            .DidNotReceive()
            .DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_WhenReceivingMessageFails_ShouldLogError()
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(FailureThenWait(new InvalidOperationException("SQS receive failed")));
        var logger = new RecordingLogger<AnalyticsEventConsumer>();
        var subject = CreateSubject(sqsClient, logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForMessage("Analytics event consumption failed", TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        Assert.Contains(
            logger.Messages,
            message => message == "Analytics event consumption failed (InvalidOperationException)"
        );
    }

    private static AnalyticsEventConsumer CreateSubject(
        IAmazonSQS sqsClient,
        ILogger<AnalyticsEventConsumer>? logger = null,
        bool processingEnabled = true,
        int pollIntervalSeconds = 1
    ) =>
        new(
            sqsClient,
            Options.Create(
                new AnalyticsEventConsumerOptions
                {
                    QueueUrl = QueueUrl,
                    ProcessingEnabled = processingEnabled,
                    WaitTimeSeconds = 1,
                    PollIntervalSeconds = pollIntervalSeconds,
                }
            ),
            logger ?? new RecordingLogger<AnalyticsEventConsumer>(),
            StartedApplication(),
            [
                new SubmittedEmailProducer(
                    Substitute.For<IWasteObligationsClient>(),
                    Options.Create(new NotificationDefinitionsOptions()),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<SubmittedEmailProducer>.Instance
                ),
                new CancellationEmailProducer(
                    new SubmittedEmailProducer(
                        Substitute.For<IWasteObligationsClient>(),
                        Options.Create(new NotificationDefinitionsOptions()),
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<SubmittedEmailProducer>.Instance
                    ),
                    Substitute.For<IWasteOrganisationsClient>(),
                    Substitute.For<IAccountBackendClient>(),
                    Options.Create(new NotificationDefinitionsOptions()),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<CancellationEmailProducer>.Instance
                ),
            ],
            new NotificationCommandPublisher(
                sqsClient,
                DeliveryOptions(),
                new NotificationCommandDigest(DeliveryOptions())
            ),
            new NotificationCommandDigest(DeliveryOptions()),
            Substitute.For<Defra.WasteObligations.Consumer.Utils.Metrics.INotificationJourneyMetrics>()
        );

    private static IOptions<NotificationCommandDeliveryOptions> DeliveryOptions() =>
        Options.Create(
            new NotificationCommandDeliveryOptions
            {
                QueueUrl = "http://sqs.test/commands.fifo",
                EvidenceDigestSecret = "test-evidence-secret",
                RecipientLaneSecret = "test-lane-secret",
            }
        );

    [Fact]
    public async Task Stop_WhenBackingOffFailedEvent_ShouldStopWithoutAnotherReceive()
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ReceiveMessageResponse>(new HttpRequestException("unsafe dependency detail")));
        var logger = new RecordingLogger<AnalyticsEventConsumer>();
        using var subject = CreateSubject(sqsClient, logger, pollIntervalSeconds: 300);
        await subject.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForMessage("Analytics event consumption failed", TestContext.Current.CancellationToken);
        await subject
            .StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(subject.ExecuteTask!.IsFaulted);
        await sqsClient.Received(1).ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
        Assert.Empty(logger.Exceptions);
    }

    private static ApplicationStartup StartedApplication()
    {
        var startup = new ApplicationStartup();
        startup.MarkStarted();

        return startup;
    }

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

    private static Func<CallInfo, Task<ReceiveMessageResponse>> FailureThenWait(Exception exception)
    {
        var receivedCount = 0;

        return call =>
        {
            if (Interlocked.Increment(ref receivedCount) == 1)
            {
                return Task.FromException<ReceiveMessageResponse>(exception);
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

    private static string Body() => $$"""{ "eventId": "{{EventId}}", "entityId": "{{EntityId}}" }""";

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

        public IReadOnlyList<Exception> Exceptions => _exceptions;

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
                    if (_messages.Any(message => message.Contains(expected, StringComparison.Ordinal)))
                    {
                        return;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }
    }
}
