using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Startup;
using Microsoft.Extensions.Configuration;
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
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
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
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
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
        using var subject = CreateSubject(sqsClient, logger, pollIntervalSeconds: 300);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await logger.WaitForMessage("Analytics event consumption failed", TestContext.Current.CancellationToken);
        await subject
            .StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subject
            .ExecuteTask!.ContinueWith(_ => { }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Single(logger.Messages, message => message == "Analytics event consumption failed");
        Assert.False(subject.ExecuteTask!.IsFaulted);
        await sqsClient.Received(1).ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Start_WhenReceiveSucceeds_ShouldImmediatelyReceiveOneMessageDespiteRemovedBatchOverride(
        bool containsMessage
    )
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QueueUrl"] = QueueUrl,
                    ["ProcessingEnabled"] = "true",
                    ["BatchSize"] = "10",
                    ["WaitTimeSeconds"] = "0",
                    ["PollIntervalSeconds"] = "300",
                }
            )
            .Build();
        var nextReceive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedCount = 0;
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                async call =>
                {
                    if (Interlocked.Increment(ref receivedCount) == 1)
                    {
                        return new ReceiveMessageResponse { Messages = containsMessage ? [CreateMessage(Body())] : [] };
                    }
                    nextReceive.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(1));

                    return new ReceiveMessageResponse();
                }
            );
        sqsClient
            .DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>())
            .Returns(new DeleteMessageResponse());
        var logger = new RecordingLogger<AnalyticsEventConsumer>();
        using var subject = CreateSubject(
            sqsClient,
            logger,
            configuredOptions: configuration.Get<AnalyticsEventConsumerOptions>()!
        );

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await nextReceive.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        await sqsClient
            .Received(2)
            .ReceiveMessageAsync(
                Arg.Is<ReceiveMessageRequest>(request => request.MaxNumberOfMessages == 1),
                Arg.Any<CancellationToken>()
            );
        await sqsClient
            .Received(containsMessage ? 1 : 0)
            .DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>());
        Assert.DoesNotContain(logger.Messages, message => message == "Analytics event consumption failed");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Start_WhenDependencyFails_ShouldBackOffAndProcessNextMessage(bool dependencyCancelled)
    {
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedCount = 0;
        var failedAt = 0L;
        var retryDelay = TimeSpan.Zero;
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                async call =>
                {
                    var count = Interlocked.Increment(ref receivedCount);
                    if (count == 1)
                    {
                        failedAt = Stopwatch.GetTimestamp();
                        if (dependencyCancelled)
                            throw new OperationCanceledException("Dependency timed out");
                        throw new InvalidOperationException("Dependency failed");
                    }
                    if (count == 2)
                    {
                        retryDelay = Stopwatch.GetElapsedTime(failedAt);

                        return new ReceiveMessageResponse { Messages = [CreateMessage(Body())] };
                    }
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(1));

                    return new ReceiveMessageResponse();
                }
            );
        sqsClient
            .DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>())
            .Returns(new DeleteMessageResponse())
            .AndDoes(_ => deleted.TrySetResult());
        var logger = new RecordingLogger<AnalyticsEventConsumer>();
        using var subject = CreateSubject(sqsClient, logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(retryDelay >= TimeSpan.FromSeconds(1));
        Assert.Single(logger.Messages, message => message == "Analytics event consumption failed");
        await sqsClient.Received(1).DeleteMessageAsync(QueueUrl, ReceiptHandle, Arg.Any<CancellationToken>());
    }

    private static AnalyticsEventConsumer CreateSubject(
        IAmazonSQS sqsClient,
        ILogger<AnalyticsEventConsumer>? logger = null,
        bool processingEnabled = true,
        int pollIntervalSeconds = 1,
        AnalyticsEventConsumerOptions? configuredOptions = null
    ) =>
        new(
            sqsClient,
            Options.Create(
                configuredOptions
                    ?? new AnalyticsEventConsumerOptions
                    {
                        QueueUrl = QueueUrl,
                        ProcessingEnabled = processingEnabled,
                        WaitTimeSeconds = 0,
                        PollIntervalSeconds = pollIntervalSeconds,
                    }
            ),
            logger ?? new RecordingLogger<AnalyticsEventConsumer>(),
            StartedApplication()
        );

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
