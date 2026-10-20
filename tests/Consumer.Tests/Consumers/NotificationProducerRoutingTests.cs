using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Notifications;
using Defra.WasteObligations.Consumer.Startup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests.Consumers;

public sealed class NotificationProducerRoutingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WhenDefinitionsMatchAnEvent_ShouldDispatchOnlyAnUnambiguousMatch(int matchingDefinitions)
    {
        var sqs = Substitute.For<IAmazonSQS>();
        var publisher = Substitute.For<INotificationCommandPublisher>();
        var digest = Substitute.For<INotificationCommandDigest>();
        var producers = Enumerable
            .Range(0, matchingDefinitions)
            .Select(_ =>
            {
                var producer = Substitute.For<IAnalyticsNotificationProducer>();
                producer.CanHandle(Arg.Any<AnalyticsEvent>()).Returns(true);
                producer
                    .Create(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<NotificationCommand>>([]));
                return producer;
            })
            .ToArray();
        var nextReceive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (Interlocked.Increment(ref received) == 1)
                    return new ReceiveMessageResponse
                    {
                        Messages =
                        [
                            new Message
                            {
                                ReceiptHandle = "receipt",
                                Body = JsonSerializer.Serialize(
                                    new
                                    {
                                        eventId = "01JZ8RXBMTY2K15SJB3PCFN3D5",
                                        entityId = "cdec_65f1f6570bb08052a8a27b01",
                                        entity = "compliance_declaration",
                                        eventType = "another.notification.event",
                                        schemaVersion = "compliance_declaration_v1.3",
                                        version = 1,
                                        after = new
                                        {
                                            organisation = new { id = "fbf1ba88-cd09-42f6-aad5-0ddfc29e5765" },
                                        },
                                    }
                                ),
                            },
                        ],
                    };
                nextReceive.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                return new ReceiveMessageResponse();
            });
        sqs.DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DeleteMessageResponse());
        var startup = new ApplicationStartup();
        startup.MarkStarted();
        using var consumer = new AnalyticsEventConsumer(
            sqs,
            Options.Create(
                new AnalyticsEventConsumerOptions
                {
                    QueueUrl = "http://sqs.test/analytics",
                    ProcessingEnabled = true,
                    WaitTimeSeconds = 1,
                    PollIntervalSeconds = 1,
                }
            ),
            NullLogger<AnalyticsEventConsumer>.Instance,
            startup,
            producers,
            publisher,
            digest
        );

        await consumer.StartAsync(TestContext.Current.CancellationToken);
        await nextReceive.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await consumer.StopAsync(TestContext.Current.CancellationToken);

        await sqs.Received(matchingDefinitions == 2 ? 0 : 1)
            .DeleteMessageAsync(Arg.Any<string>(), "receipt", Arg.Any<CancellationToken>());
        foreach (var producer in producers)
            await producer
                .Received(matchingDefinitions == 1 ? 1 : 0)
                .Create(Arg.Any<Guid>(), "65f1f6570bb08052a8a27b01", 1, Arg.Any<CancellationToken>());
    }
}
