using System.Collections.Concurrent;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Startup;
using Defra.WasteObligations.Consumer.Utils.Metrics;
using Elastic.CommonSchema.Serilog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Defra.WasteObligations.Consumer.Tests.Consumers;

public sealed class AnalyticsTraceCorrelationTests
{
    [Theory]
    [InlineData("\"4bf92f3577b34da6a3ce929d0e0e4736\"", "4bf92f3577b34da6a3ce929d0e0e4736")]
    [InlineData("\"59a48ed1-c28b-4126-bbc5-46b0461798b8\"", "59a48ed1-c28b-4126-bbc5-46b0461798b8")]
    [InlineData("null", null)]
    [InlineData("42", null)]
    [InlineData("\"private-recipient@example.invalid\"", null)]
    public async Task WhenCorrelatedEventWillRetry_ShouldWarnWithEcsTraceAndClearItBeforeNextMessage(
        string correlationJson,
        string? expectedTrace
    )
    {
        var capture = new Capture();
        using var serilog = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(capture).CreateLogger();
        using var logs = new SerilogLoggerFactory(serilog, dispose: false);
        var sqs = Substitute.For<IAmazonSQS>();
        var receives = 0;
        sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var attempt = Interlocked.Increment(ref receives);
                if (attempt <= 2)
                    return new ReceiveMessageResponse
                    {
                        Messages =
                        [
                            new Message
                            {
                                MessageId = "63922ee7-819a-4bb0-87fb-e3716a58b327",
                                ReceiptHandle = "receipt",
                                Body =
                                    attempt == 1
                                        ? EventBody(correlationJson)
                                        : "{\"eventId\":\"01JZ8RXBMTY2K15SJB3PCFN3D6\",\"entityId\":\"other\"}",
                            },
                        ],
                    };

                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());

                return new ReceiveMessageResponse();
            });
        var deletions = 0;
        sqs.DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
                ++deletions == 1
                    ? Task.FromException<DeleteMessageResponse>(
                        new InvalidOperationException("private-recipient@example.invalid")
                    )
                    : Task.FromResult(new DeleteMessageResponse())
            );
        var startup = new ApplicationStartup();
        startup.MarkStarted();
        using var consumer = new AnalyticsEventConsumer(
            sqs,
            Options.Create(
                new AnalyticsEventConsumerOptions
                {
                    ProcessingEnabled = true,
                    QueueUrl = "http://localhost/analytics",
                    WaitTimeSeconds = 1,
                    PollIntervalSeconds = 1,
                }
            ),
            logs.CreateLogger<AnalyticsEventConsumer>(),
            startup,
            [],
            Substitute.For<INotificationCommandPublisher>(),
            Substitute.For<INotificationCommandDigest>(),
            Substitute.For<INotificationJourneyMetrics>()
        );
        try
        {
            await consumer.StartAsync(TestContext.Current.CancellationToken);
            await capture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            await consumer.StopAsync(TestContext.Current.CancellationToken);
        }

        var failure = Assert.Single(capture.Events, entry => entry.Level == LogEventLevel.Warning);
        Assert.Null(failure.Exception);
        Assert.Equal("dependency-failure", ((ScalarValue)failure.Properties["FailureReason"]).Value);
        Assert.Equal("acknowledge", ((ScalarValue)failure.Properties["Stage"]).Value);
        Assert.Contains("acknowledgement uncertain", failure.RenderMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(capture.Events, entry => entry.Level >= LogEventLevel.Error);
        AssertTrace(failure, expectedTrace);
        var success = Assert.Single(
            capture.Events,
            entry => entry.MessageTemplate.Text.StartsWith("Consumed analytics event", StringComparison.Ordinal)
        );
        AssertTrace(success, null);
        Assert.All(
            capture.Events,
            entry => Assert.DoesNotContain("private-recipient", Format(entry), StringComparison.Ordinal)
        );
    }

    private static string EventBody(string correlationJson) =>
        "{\"eventId\":\"01JZ8RXBMTY2K15SJB3PCFN3D5\",\"entityId\":\"other\",\"correlationId\":" + correlationJson + "}";

    private static void AssertTrace(LogEvent entry, string? expected)
    {
        using var json = JsonDocument.Parse(Format(entry));
        var actual = json.RootElement.TryGetProperty("trace.id", out var trace) ? trace.GetString() : null;
        Assert.Equal(expected, actual);
    }

    private static string Format(LogEvent entry)
    {
        using var writer = new StringWriter();
        new EcsTextFormatter().Format(entry, writer);

        return writer.ToString();
    }

    private sealed class Capture : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Emit(LogEvent logEvent)
        {
            Events.Enqueue(logEvent);
            if (logEvent.MessageTemplate.Text.StartsWith("Consumed analytics event", StringComparison.Ordinal))
                Completed.TrySetResult();
        }
    }
}
