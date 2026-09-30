using System.Net;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.Core;

namespace Defra.WasteObligations.Consumer.Tests.Commands;

public class NotificationCommandPublisherTests
{
    [Theory]
    [InlineData("set-automatically-by-deployment")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Publish_WhenProcessingIsDisabledAndLaneSecretIsUnconfigured_ShouldNotQueueCommand(string secret)
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        var options = CreateOptions() with { ProcessingEnabled = false, RecipientLaneSecret = secret };
        var subject = new NotificationCommandPublisher(
            sqsClient,
            Options.Create(options),
            new NotificationCommandDigest(Options.Create(options))
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            subject.Publish(CreateCommand(), TestContext.Current.CancellationToken)
        );

        if (!string.IsNullOrWhiteSpace(secret))
            Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        await sqsClient.DidNotReceive().SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publish_ShouldNormaliseRecipientAndUseStableFifoIdentifiers()
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient
            .SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendMessageResponse { HttpStatusCode = HttpStatusCode.OK });
        var subject = new NotificationCommandPublisher(
            sqsClient,
            Options.Create(CreateOptions()),
            new NotificationCommandDigest(Options.Create(CreateOptions()))
        );

        await subject.Publish(CreateCommand(), TestContext.Current.CancellationToken);
        await subject.Publish(CreateCommand(), TestContext.Current.CancellationToken);

        var firstRequest = sqsClient.ReceivedCalls().First().GetArguments().OfType<SendMessageRequest>().Single();
        var secondRequest = sqsClient
            .ReceivedCalls()
            .Skip(1)
            .First()
            .GetArguments()
            .OfType<SendMessageRequest>()
            .Single();
        using var body = JsonDocument.Parse(firstRequest.MessageBody);

        Assert.Equal("command-key-1", firstRequest.MessageDeduplicationId);
        Assert.Equal(firstRequest.MessageGroupId, secondRequest.MessageGroupId);
        Assert.NotEqual("recipient@example.com", firstRequest.MessageGroupId);
        Assert.Equal("recipient@example.com", body.RootElement.GetProperty("emailAddress").GetString());
    }

    private static NotificationCommand CreateCommand() =>
        new(
            NotificationCommand.CurrentSchemaVersion,
            "command-key-1",
            new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero),
            "declaration-submitted",
            " Recipient@Example.com ",
            "template-1",
            JsonDocument.Parse("{}").RootElement.Clone()
        );

    private static NotificationCommandDeliveryOptions CreateOptions() =>
        new()
        {
            QueueUrl = "http://localhost:4566/000000000000/commands.fifo",
            EmailDeliveryCutoverUtc = "2026-09-29T00:00:00Z",
            EvidenceDigestSecret = "test-evidence-secret",
            RecipientLaneSecret = "test-recipient-lane-secret",
        };
}
