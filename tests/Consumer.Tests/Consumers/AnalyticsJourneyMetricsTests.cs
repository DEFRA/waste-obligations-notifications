using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Notifications;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
using Defra.WasteObligations.Consumer.Startup;
using Defra.WasteObligations.Consumer.Utils.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using CommandMetrics = Defra.WasteObligations.Consumer.Utils.Metrics.Metrics;

namespace Defra.WasteObligations.Consumer.Tests.Consumers;

public sealed class AnalyticsJourneyMetricsTests
{
    [Theory]
    [InlineData("published", 1, 2, "commands-published", null)]
    [InlineData("replayed", 2, 4, "commands-published", null)]
    [InlineData("ignored", 1, 0, "ignored", null)]
    [InlineData("no-notifications", 1, 0, "no-notifications", null)]
    [InlineData("invalid", 1, 0, null, "parse")]
    [InlineData("enrichment-failed", 1, 0, null, "read-declaration")]
    [InlineData("unknown-cancellation-reason", 1, 0, null, "read-declaration")]
    [InlineData("publication-failed", 1, 1, null, "publish-command")]
    [InlineData("acknowledgement-failed", 1, 2, null, "acknowledge")]
    [InlineData("receive-failed", 0, 0, null, "receive")]
    public async Task WhenAnalyticsIsHandled_ShouldRecordConfirmedJourneyProgress(
        string scenario,
        int expectedReceipts,
        int expectedPublications,
        string? expectedOutcome,
        string? expectedFailureStage
    )
    {
        using var services = new ServiceCollection().AddNotificationJourneyMetrics().BuildServiceProvider();
        var meter = services.GetRequiredService<IMeterFactory>().Create(CommandMetrics.MeterName);
        var measurements = new ConcurrentQueue<(string Name, double Value, KeyValuePair<string, object?>[] Tags)>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (ReferenceEquals(instrument.Meter, meter))
                observer.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) =>
            {
                measurements.Enqueue((instrument.Name, value, tags.ToArray()));
                if (
                    instrument.Name == MetricNames.NotificationProcessingFailure
                    || instrument.Name == MetricNames.AnalyticsEventOutcome
                        && measurements.Count(measurement => measurement.Name == MetricNames.AnalyticsEventOutcome)
                            == expectedReceipts
                )
                    completed.TrySetResult();
            }
        );
        listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) => measurements.Enqueue((instrument.Name, value, tags.ToArray()))
        );
        listener.Start();

        var sqs = Substitute.For<IAmazonSQS>();
        var receives = 0;
        sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var receive = Interlocked.Increment(ref receives);
                if (scenario == "receive-failed" && receive == 1)
                    throw new InvalidOperationException("private dependency response");
                if (receive <= expectedReceipts)
                    return new ReceiveMessageResponse
                    {
                        Messages =
                        [
                            new Message
                            {
                                MessageId = "message-1",
                                ReceiptHandle = "receipt-1",
                                Body = scenario == "invalid" ? "invalid-json" : EventBody(),
                            },
                        ],
                    };

                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());

                return new ReceiveMessageResponse();
            });
        sqs.DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
                scenario == "acknowledgement-failed"
                    ? Task.FromException<DeleteMessageResponse>(new InvalidOperationException("private queue response"))
                    : Task.FromResult(new DeleteMessageResponse())
            );
        var producer = Substitute.For<IAnalyticsNotificationProducer>();
        IReadOnlyList<NotificationCommand> commands =
            scenario == "no-notifications" ? [] : [Command("first"), Command("second")];
        producer.CanHandle(Arg.Any<AnalyticsEvent>()).Returns(scenario != "ignored");
        Exception preparationFailure =
            scenario == "unknown-cancellation-reason"
                ? new Defra.WasteObligations.Consumer.Submission.SubmissionProcessingException(
                    "unknown-cancellation-reason"
                )
                : new InvalidOperationException("private declaration response");
        producer
            .Create(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Declaration?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ =>
                scenario is "enrichment-failed" or "unknown-cancellation-reason"
                    ? Task.FromException<IReadOnlyList<NotificationCommand>>(preparationFailure)
                    : Task.FromResult(commands)
            );
        var publisher = Substitute.For<INotificationCommandPublisher>();
        var publications = 0;
        publisher
            .Publish(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
                scenario == "publication-failed" && Interlocked.Increment(ref publications) == 2
                    ? Task.FromException(new InvalidOperationException("private publication response"))
                    : Task.CompletedTask
            );
        var startup = new ApplicationStartup();
        startup.MarkStarted();
        using var consumer = new AnalyticsEventConsumer(
            sqs,
            Options.Create(
                new AnalyticsEventConsumerOptions
                {
                    QueueUrl = "http://localhost/analytics",
                    ProcessingEnabled = true,
                    WaitTimeSeconds = 1,
                    PollIntervalSeconds = 1,
                }
            ),
            NullLogger<AnalyticsEventConsumer>.Instance,
            startup,
            [producer],
            publisher,
            Substitute.For<INotificationCommandDigest>(),
            services.GetRequiredService<INotificationJourneyMetrics>()
        );

        await consumer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            await consumer.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            expectedReceipts,
            measurements
                .Where(measurement => measurement.Name == MetricNames.AnalyticsEventReceived)
                .Sum(measurement => measurement.Value)
        );
        Assert.Equal(
            expectedPublications,
            measurements
                .Where(measurement => measurement.Name == MetricNames.NotificationCommandPublished)
                .Sum(measurement => measurement.Value)
        );
        Assert.All(
            publisher.ReceivedCalls(),
            call =>
                Assert.Equal(
                    "4bf92f3577b34da6a3ce929d0e0e4736",
                    ((NotificationCommand)call.GetArguments()[0]!).CorrelationId
                )
        );
        var outcomes = measurements
            .Where(measurement => measurement.Name == MetricNames.AnalyticsEventOutcome)
            .ToArray();
        Assert.Equal(expectedOutcome is null ? 0 : expectedReceipts, outcomes.Length);
        Assert.All(
            outcomes,
            measurement =>
                Assert.Contains(
                    measurement.Tags,
                    tag => tag.Key == MetricTags.Outcome && Equals(tag.Value, expectedOutcome)
                )
        );
        var failures = measurements
            .Where(measurement => measurement.Name == MetricNames.NotificationProcessingFailure)
            .ToArray();
        if (expectedFailureStage is null)
            Assert.Empty(failures);
        else
        {
            var failure = Assert.Single(failures);
            var expectedReason = scenario switch
            {
                "invalid" => "invalid-event-data",
                "publication-failed" => "unconfirmed-publication",
                "unknown-cancellation-reason" => "unknown-cancellation-reason",
                _ => "dependency-failure",
            };
            Assert.Contains(
                failure.Tags,
                tag => tag.Key == MetricTags.FailureReason && Equals(tag.Value, expectedReason)
            );
            Assert.Contains(failure.Tags, tag => tag.Key == MetricTags.Consumer && Equals(tag.Value, "analytics"));
            Assert.Contains(
                failure.Tags,
                tag => tag.Key == MetricTags.Stage && Equals(tag.Value, expectedFailureStage)
            );
        }
        Assert.Equal(
            expectedReceipts,
            measurements.Count(measurement => measurement.Name == MetricNames.AnalyticsEventProcessingDuration)
        );
        Assert.Equal(
            expectedReceipts,
            measurements
                .Where(measurement => measurement.Name == MetricNames.NotificationConsumerPoll)
                .Sum(measurement => measurement.Value)
        );
        Assert.All(
            measurements.SelectMany(measurement => measurement.Tags),
            tag =>
                Assert.True(
                    tag.Key
                        is MetricTags.Service
                            or MetricTags.Consumer
                            or MetricTags.Stage
                            or MetricTags.FailureReason
                            or MetricTags.Source
                            or MetricTags.Outcome
                )
        );
    }

    private static NotificationCommand Command(string key) =>
        new(
            NotificationCommand.CurrentSchemaVersion,
            key,
            new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero),
            "declaration-submitted",
            "recipient@example.invalid",
            "01234567-89ab-cdef-0123-456789abcdef",
            JsonSerializer.SerializeToElement(new { value = "private personalisation" }),
            CorrelationId: "59a48ed1-c28b-4126-bbc5-46b0461798b8"
        );

    private static string EventBody() =>
        """
            { "correlationId":"4bf92f3577b34da6a3ce929d0e0e4736","eventId": "01JZ8RXBMTY2K15SJB3PCFN3D5", "entityId": "cdec_65f1f6570bb08052a8a27b01", "entity": "compliance_declaration", "eventType": "submission.created", "schemaVersion": "compliance_declaration_v1.0", "version": 1, "after": { "organisation": { "id": "01234567-89ab-cdef-0123-456789abcdef" } } }
            """;
}
