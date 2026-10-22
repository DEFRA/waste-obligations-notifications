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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using LiveOrganisation = Defra.WasteObligations.Consumer.Services.WasteOrganisations.Organisation;

namespace Defra.WasteObligations.Consumer.Tests.Cancellation;

public sealed class CancellationPublicationDeadlineTests
{
    [Fact]
    public async Task WhenPredecessorCompletesAfterPublicationDeadline_ShouldNeverPublishCancellationOrAcknowledge()
    {
        const string declarationId = "65f1f6570bb08052a8a27b01";
        var organisationId = Guid.NewGuid();
        var reader = Substitute.For<IWasteObligationsClient>();
        reader
            .ReadDeclaration(organisationId, declarationId, Arg.Any<CancellationToken>())
            .Returns(
                new Declaration(
                    declarationId,
                    2,
                    2026,
                    new DeclarationOrganisation(
                        organisationId.ToString(),
                        "GB-ENG",
                        "DirectProducer",
                        "EA",
                        "regulator@example.test"
                    ),
                    [
                        new DeclarationAudit(
                            "Submitted",
                            "2026-09-20T11:12:13Z",
                            new DeclarationUser("Original", "submitter@example.test")
                        ),
                        new DeclarationAudit("Cancelled", "2026-10-01T12:00:00Z", null, "Producer requested to cancel"),
                    ]
                )
            );
        var live = Substitute.For<IWasteOrganisationsClient>();
        live.Read(organisationId, Arg.Any<CancellationToken>())
            .Returns(new LiveOrganisation(organisationId, "GB-ENG", null));
        var accounts = Substitute.For<IAccountBackendClient>();
        accounts
            .ReadOrganisationWithPersons(organisationId, Arg.Any<CancellationToken>())
            .Returns(
                new OrganisationWithPersons([
                    new OrganisationPerson(null, "First", "Last", "submitter@example.test", "Approved Person"),
                ])
            );
        var submitted = new SubmittedEmailProducer(reader, Options.Create(new NotificationDefinitionsOptions()));
        var cancellation = new CancellationEmailProducer(
            submitted,
            live,
            accounts,
            Options.Create(new NotificationDefinitionsOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CancellationEmailProducer>.Instance
        );
        var publisher = Substitute.For<INotificationCommandPublisher>();
        var latePredecessor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadlineReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher
            .Publish(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().Register(() => deadlineReached.TrySetResult());

                return latePredecessor.Task;
            });
        var receivedAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sqs = Substitute.For<IAmazonSQS>();
        var receives = 0;
        sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (Interlocked.Increment(ref receives) == 1)
                    return new ReceiveMessageResponse
                    {
                        Messages =
                        [
                            new Message
                            {
                                ReceiptHandle = "receipt",
                                Body = System.Text.Json.JsonSerializer.Serialize(
                                    new
                                    {
                                        eventId = "01K5A7M9Q2X6F4N8C0V3B1D5E7",
                                        entityId = $"cdec_{declarationId}",
                                        entity = "compliance_declaration",
                                        eventType = "submission.amended",
                                        schemaVersion = "compliance_declaration_v1.3",
                                        version = 2,
                                        before = new { status = "Submitted" },
                                        after = new
                                        {
                                            status = "Cancelled",
                                            organisation = new { id = organisationId },
                                        },
                                    }
                                ),
                            },
                        ],
                    };
                receivedAgain.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());

                return new ReceiveMessageResponse();
            });
        var startup = new ApplicationStartup();
        startup.MarkStarted();
        var digest = new NotificationCommandDigest(
            Options.Create(
                new NotificationCommandDeliveryOptions
                {
                    QueueUrl = "http://sqs.test/commands.fifo",
                    EvidenceDigestSecret = "test-evidence-secret",
                    RecipientLaneSecret = "test-lane-secret",
                }
            )
        );
        using var consumer = new AnalyticsEventConsumer(
            sqs,
            Options.Create(
                new AnalyticsEventConsumerOptions
                {
                    QueueUrl = "http://sqs.test/analytics",
                    ProcessingEnabled = true,
                    WaitTimeSeconds = 1,
                    ReadTimeoutSeconds = 1,
                    PublishTimeoutSeconds = 1,
                    PollIntervalSeconds = 1,
                }
            ),
            NullLogger<AnalyticsEventConsumer>.Instance,
            startup,
            [submitted, cancellation],
            publisher,
            digest
        );
        await consumer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await deadlineReached.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            latePredecessor.SetResult();
            await receivedAgain.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            await publisher
                .Received(1)
                .Publish(
                    Arg.Is<NotificationCommand>(command =>
                        command.NotificationType.StartsWith("declaration-submitted-")
                    ),
                    Arg.Any<CancellationToken>()
                );
            await publisher
                .DidNotReceive()
                .Publish(
                    Arg.Is<NotificationCommand>(command =>
                        command.NotificationType.StartsWith("declaration-cancelled-")
                    ),
                    Arg.Any<CancellationToken>()
                );
            await sqs.DidNotReceive()
                .DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            latePredecessor.TrySetResult();
            await consumer.StopAsync(CancellationToken.None);
        }
    }
}
