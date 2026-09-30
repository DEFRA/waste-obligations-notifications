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
    public static TheoryData<string> ValidFifoIdempotencyKeys =>
        new() { new string('k', 128), """!"#$%&'()*+,-./:;<=>?@[\]^_`{|}~""" };

    public static TheoryData<string> InvalidFifoIdempotencyKeys =>
        new()
        {
            new string('k', 129),
            "key with space",
            "key\tvalue",
            "key\nvalue",
            "key\u0000value",
            "key\u007fvalue",
            "keyévalue",
            "key😀value",
        };

    [Theory]
    [MemberData(nameof(ValidFifoIdempotencyKeys))]
    public async Task Publish_WhenIdempotencyKeyFitsFifoConstraints_ShouldPreserveItUnchanged(string key)
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

        var command = CreateCommand() with
        {
            IdempotencyKey = key,
            ActionOccurredAtUtc = CreateCommand().ActionOccurredAtUtc.AddTicks(1234567),
        };
        await subject.Publish(command, TestContext.Current.CancellationToken);

        await sqsClient
            .Received(1)
            .SendMessageAsync(
                Arg.Is<SendMessageRequest>(request => request.MessageDeduplicationId == key),
                Arg.Any<CancellationToken>()
            );
        var request = sqsClient.ReceivedCalls().Single().GetArguments().OfType<SendMessageRequest>().Single();
        using var body = JsonDocument.Parse(request.MessageBody);
        Assert.Equal(key, body.RootElement.GetProperty("idempotencyKey").GetString());
        Assert.Equal(
            CreateCommand().ActionOccurredAtUtc.AddMilliseconds(123),
            NotificationCommandMessageReader.Read(new Message { Body = request.MessageBody }).ActionOccurredAtUtc
        );
        Assert.Equal(
            CreateCommand().ActionOccurredAtUtc.AddMilliseconds(123),
            body.RootElement.GetProperty("actionOccurredAtUtc").GetDateTimeOffset()
        );
    }

    [Fact]
    public async Task Publish_WhenActionTimestampHasNonzeroOffset_ShouldRejectWithoutQueuing()
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        var subject = new NotificationCommandPublisher(
            sqsClient,
            Options.Create(CreateOptions()),
            new NotificationCommandDigest(Options.Create(CreateOptions()))
        );
        var command = CreateCommand();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            subject.Publish(
                command with
                {
                    ActionOccurredAtUtc = command.ActionOccurredAtUtc.ToOffset(TimeSpan.FromHours(1)),
                },
                TestContext.Current.CancellationToken
            )
        );

        await sqsClient.DidNotReceive().SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9999)]
    public async Task Publish_WhenActionTimestampTruncatesToDefault_ShouldRejectWithoutQueuing(int ticks)
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        var subject = new NotificationCommandPublisher(
            sqsClient,
            Options.Create(CreateOptions()),
            new NotificationCommandDigest(Options.Create(CreateOptions()))
        );

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            subject.Publish(
                CreateCommand() with
                {
                    ActionOccurredAtUtc = DateTimeOffset.MinValue.AddTicks(ticks),
                },
                TestContext.Current.CancellationToken
            )
        );

        await sqsClient.DidNotReceive().SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(InvalidFifoIdempotencyKeys))]
    public async Task Publish_WhenIdempotencyKeyIsInvalid_ShouldRejectWithoutQueuingOrExposingKey(string key)
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        var subject = new NotificationCommandPublisher(
            sqsClient,
            Options.Create(CreateOptions()),
            new NotificationCommandDigest(Options.Create(CreateOptions()))
        );

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            subject.Publish(CreateCommand() with { IdempotencyKey = key }, TestContext.Current.CancellationToken)
        );

        Assert.DoesNotContain(key, exception.Message, StringComparison.Ordinal);
        await sqsClient.DidNotReceive().SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
    }

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
