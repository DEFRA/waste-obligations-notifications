using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Administration;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests.Administration;

public sealed class CommandDlqDiscardTests
{
    private static readonly JsonSerializerOptions s_indentedJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private const string Route = "/admin/notification-commands/dlq/discard";
    private const string Secret = "private-päss:secret";
    private const string Identity = "private-recipient@example.com";
    private const string PrivateContent = "private-personalisation-template-notify";
    private const string Receipt = "private-receipt-handle";

    [Fact]
    public async Task WhenAdminDiscardsSelection_ShouldRecordAbandonmentBeforeDeletingOnlySelectedReceipt()
    {
        await using var factory = new DiscardApplicationFactory();
        using var client = factory.CreateClient();
        var token = Selection(factory);
        var order = new List<string>();
        factory
            .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Add("receive");
                return new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.OK, Messages = [Message()] };
            });
        factory
            .Store.RecordAbandonment(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Add("abandon");
                return AbandonmentResult.Recorded;
            });
        factory
            .Sqs.DeleteMessageAsync(Arg.Any<DeleteMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                order.Add("delete");
                return new DeleteMessageResponse { HttpStatusCode = HttpStatusCode.OK };
            });
        using var request = Request(token);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["receive", "abandon", "delete"], order);
        var deleted = Assert.IsType<DeleteMessageRequest>(factory.Sqs.ReceivedCalls().Last().GetArguments()[0]);
        Assert.Equal("http://sqs.local/commands-dlq.fifo", deleted.QueueUrl);
        Assert.Equal(Receipt, deleted.ReceiptHandle);
        var abandoned = Assert.IsType<NotificationCommand>(
            Assert.Single(factory.Store.ReceivedCalls()).GetArguments()[0]
        );
        Assert.Equal(Identity, abandoned.EmailAddress);
        await AssertPrivacy(factory, response, token);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("Bearer private-token", HttpStatusCode.Unauthorized)]
    [InlineData("Basic: private-header", HttpStatusCode.Unauthorized)]
    [InlineData("Basic !!!", HttpStatusCode.Unauthorized)]
    [InlineData("wrong", HttpStatusCode.Unauthorized)]
    [InlineData("invalid-utf8", HttpStatusCode.Unauthorized)]
    [InlineData("multiple", HttpStatusCode.Unauthorized)]
    [InlineData("oauth", HttpStatusCode.Unauthorized)]
    [InlineData("read", HttpStatusCode.Forbidden)]
    [InlineData("write", HttpStatusCode.Forbidden)]
    public async Task WhenCallerIsNotAdmin_ShouldDenyBeforeAnyDependency(string? credential, HttpStatusCode expected)
    {
        await using var factory = new DiscardApplicationFactory();
        using var client = factory.CreateClient();
        var token = Selection(factory);
        using var request = Request(
            token,
            credential switch
            {
                "wrong" => Basic("admin", "wrong-private-secret"),
                "invalid-utf8" => "Basic " + Convert.ToBase64String(new byte[] { 0xff, 0xfe, 0x3a }),
                "multiple" => Basic("admin", Secret),
                "oauth" or "read" or "write" => Basic(credential, Secret),
                _ => credential ?? "",
            }
        );

        if (credential == "multiple")
            request.Headers.TryAddWithoutValidation("Authorization", Basic("admin", Secret));
        if (credential is null)
            request.Headers.Remove("Authorization");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
        await AssertPrivacy(factory, response, token);
    }

    [Fact]
    public async Task WhenAdministrationIsDisabled_ShouldExposeNoDiscardSurface()
    {
        await using var factory = new DiscardApplicationFactory(
            new() { ["CommandDlqAdministration:Enabled"] = "false" }
        );
        using var client = factory.CreateClient();
        using var request = Request("private-selection-token");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
        await AssertPrivacy(factory, response, "private-selection-token");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("signature")]
    [InlineData("expired")]
    [InlineData("queue")]
    [InlineData("secret")]
    public async Task WhenSelectionIsInvalid_ShouldRefuseBeforeReplay(string condition)
    {
        await using var issuer = new DiscardApplicationFactory();
        using var issuerClient = issuer.CreateClient();
        var token = Selection(
            issuer,
            expires: DateTimeOffset.UtcNow.AddMilliseconds(condition == "expired" ? 100 : 100000)
        );
        if (condition == "missing")
            token = null;
        if (condition == "malformed")
            token = "private-invalid-token";
        if (condition == "signature")
            token += "x";
        if (condition == "expired")
            await Task.Delay(200, TestContext.Current.CancellationToken);
        await using var target = new DiscardApplicationFactory(
            condition switch
            {
                "queue" => new() { ["CommandDlqAdministration:QueueUrl"] = "http://sqs.local/another-dlq.fifo" },
                "secret" => new() { ["NotificationCommandDelivery:EvidenceDigestSecret"] = "another-evidence-secret" },
                _ => null,
            }
        );
        using var client = target.CreateClient();
        using var request = Request(token);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(target.Sqs.ReceivedCalls());
        Assert.Empty(target.Store.ReceivedCalls());
        await AssertPrivacy(target, response, token);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("identity")]
    [InlineData("receipt")]
    [InlineData("malformed")]
    [InlineData("unsupported")]
    [InlineData("changed")]
    public async Task WhenReplayCannotConfirmSelection_ShouldNeverAbandonOrDelete(string condition)
    {
        await using var factory = new DiscardApplicationFactory();
        using var client = factory.CreateClient();
        var token = Selection(factory);
        var message = Message();
        if (condition == "identity")
            message.MessageId = "different-message";
        if (condition == "receipt")
            message.ReceiptHandle = null;
        if (condition == "malformed")
            message.Body = $"not JSON {PrivateContent}";
        if (condition == "unsupported")
            message.Body = message.Body.Replace(
                "\"schemaVersion\": 1",
                "\"schemaVersion\": 2",
                StringComparison.Ordinal
            );
        if (condition == "changed")
            message.Body = message.Body.Replace(PrivateContent, "changed-private-content", StringComparison.Ordinal);
        factory
            .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReceiveMessageResponse
                {
                    HttpStatusCode = HttpStatusCode.OK,
                    Messages = condition == "empty" ? [] : [message],
                }
            );
        using var request = Request(token);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
        await AssertPrivacy(factory, response, token);
    }

    [Fact]
    public async Task WhenAbandonmentIsStillInFlight_ShouldNotDeleteBeforeDurableConfirmation()
    {
        await using var factory = new DiscardApplicationFactory();
        using var client = factory.CreateClient();
        var token = Selection(factory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmation = new TaskCompletionSource<AbandonmentResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        factory
            .Store.RecordAbandonment(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                entered.TrySetResult();
                return confirmation.Task.WaitAsync(call.ArgAt<CancellationToken>(1));
            });
        using var request = Request(token);
        var pending = client.SendAsync(request, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(pending.IsCompleted);
            Assert.Single(factory.Sqs.ReceivedCalls());
        }
        finally
        {
            confirmation.TrySetResult(AbandonmentResult.Recorded);
        }
        using var response = await pending;

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(2, factory.Sqs.ReceivedCalls().Count());
        await AssertPrivacy(factory, response, token);
    }

    [Fact]
    public async Task WhenRecordGuardRefusesDiscard_ShouldPreserveSourceWithoutAnotherOperation()
    {
        await using var factory = new DiscardApplicationFactory();
        using var client = factory.CreateClient();
        var token = Selection(factory);
        factory
            .Store.RecordAbandonment(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(AbandonmentResult.Conflict);
        using var request = Request(token);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(factory.Sqs.ReceivedCalls());
        Assert.Single(factory.Store.ReceivedCalls());
        await AssertPrivacy(factory, response, token);
    }

    [Theory]
    [InlineData("receive")]
    [InlineData("receive-status")]
    [InlineData("receive-many")]
    [InlineData("abandon")]
    [InlineData("delete")]
    [InlineData("delete-status")]
    public async Task WhenDependencyFails_ShouldExposeSafeFailureAndNeverDeleteBeforeAbandonment(string condition)
    {
        await using var factory = new DiscardApplicationFactory();
        using var client = factory.CreateClient();
        var token = Selection(factory);
        var poisoned = new InvalidOperationException($"{Identity} {PrivateContent} {Receipt} {token}");
        if (condition == "receive")
            factory
                .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<ReceiveMessageResponse>(poisoned));
        if (condition == "receive-status")
            factory
                .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.BadGateway });
        if (condition == "receive-many")
            factory
                .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(
                    new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.OK, Messages = [Message(), Message()] }
                );
        if (condition == "abandon")
            factory
                .Store.RecordAbandonment(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<AbandonmentResult>(poisoned));
        if (condition == "delete")
            factory
                .Sqs.DeleteMessageAsync(Arg.Any<DeleteMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<DeleteMessageResponse>(poisoned));
        if (condition == "delete-status")
            factory
                .Sqs.DeleteMessageAsync(Arg.Any<DeleteMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(new DeleteMessageResponse { HttpStatusCode = HttpStatusCode.BadGateway });
        using var request = Request(token);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            condition.StartsWith("delete", StringComparison.Ordinal) ? 2 : 1,
            factory.Sqs.ReceivedCalls().Count()
        );
        Assert.Equal(
            condition.StartsWith("receive", StringComparison.Ordinal) ? 0 : 1,
            factory.Store.ReceivedCalls().Count()
        );
        await AssertPrivacy(factory, response, token);
    }

    [Theory]
    [InlineData("receive", false)]
    [InlineData("abandon", false)]
    [InlineData("delete", false)]
    [InlineData("receive", true)]
    [InlineData("abandon", true)]
    [InlineData("delete", true)]
    public async Task WhenDependencyTimesOutOrConfirmsLate_ShouldNotStartAnyFurtherEffect(
        string stage,
        bool ignoreCancellation
    )
    {
        await using var factory = new DiscardApplicationFactory(
            new() { ["CommandDlqAdministration:DependencyTimeoutSeconds"] = "1" }
        );
        using var client = factory.CreateClient();
        var token = Selection(factory);
        var cancelled = false;
        async Task Delay(CancellationToken cancellationToken)
        {
            if (ignoreCancellation)
                await Task.Delay(1150, TestContext.Current.CancellationToken);
            else
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    throw;
                }
            }
        }
        if (stage == "receive")
            factory
                .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    await Delay(call.ArgAt<CancellationToken>(1));
                    return new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.OK, Messages = [Message()] };
                });
        if (stage == "abandon")
            factory
                .Store.RecordAbandonment(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    await Delay(call.ArgAt<CancellationToken>(1));
                    return AbandonmentResult.Recorded;
                });
        if (stage == "delete")
            factory
                .Sqs.DeleteMessageAsync(Arg.Any<DeleteMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    await Delay(call.ArgAt<CancellationToken>(1));
                    return new DeleteMessageResponse { HttpStatusCode = HttpStatusCode.OK };
                });
        using var request = Request(token);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(!ignoreCancellation, cancelled);
        Assert.Equal(stage == "delete" ? 2 : 1, factory.Sqs.ReceivedCalls().Count());
        Assert.Equal(stage == "receive" ? 0 : 1, factory.Store.ReceivedCalls().Count());
        await AssertPrivacy(factory, response, token);
    }

    [Fact]
    public async Task WhenCallerCancelsAbandonment_ShouldCancelActualWriteWithoutDeletion()
    {
        await using var factory = new DiscardApplicationFactory();
        using var client = factory.CreateClient();
        var token = Selection(factory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory
            .Store.RecordAbandonment(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(1));
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }
                return AbandonmentResult.Recorded;
            });
        using var request = Request(token);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = client.SendAsync(request, source.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Single(factory.Sqs.ReceivedCalls());
        Assert.Single(factory.Store.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
        Assert.DoesNotContain(
            factory.Sqs.ReceivedCalls(),
            call => call.GetMethodInfo().Name == nameof(IAmazonSQS.SendMessageAsync)
        );
    }

    [Fact]
    public async Task WhenSelectionExpiresDuringAbandonment_ShouldRetainSourceEvenIfWriteConfirms()
    {
        await using var factory = new DiscardApplicationFactory();
        using var client = factory.CreateClient();
        var token = Selection(factory, expires: DateTimeOffset.UtcNow.AddMilliseconds(350));
        CancellationToken writeToken = default;
        factory
            .Store.RecordAbandonment(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                writeToken = call.ArgAt<CancellationToken>(1);
                await Task.Delay(500, TestContext.Current.CancellationToken);
                return AbandonmentResult.Recorded;
            });
        using var request = Request(token);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(writeToken.IsCancellationRequested);
        Assert.Single(factory.Sqs.ReceivedCalls());
        Assert.Single(factory.Store.ReceivedCalls());
        await AssertPrivacy(factory, response, token);
    }

    [Fact]
    public async Task WhenDeleteFailsAfterAbandonment_ShouldCompleteIdempotentRemovalOnAnotherHost()
    {
        await using var first = new DiscardApplicationFactory();
        using var firstClient = first.CreateClient();
        var token = Selection(first);
        first
            .Sqs.DeleteMessageAsync(Arg.Any<DeleteMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DeleteMessageResponse { HttpStatusCode = HttpStatusCode.BadGateway });
        using var firstRequest = Request(token);
        using var failed = await firstClient.SendAsync(firstRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Single(first.Store.ReceivedCalls());
        await using var second = new DiscardApplicationFactory(
            new() { ["CommandDlqAdministration:SelectionLifetimeSeconds"] = "60" }
        );
        using var secondClient = second.CreateClient();
        second
            .Store.RecordAbandonment(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(AbandonmentResult.AlreadyAbandoned);
        using var retry = Request(token);

        using var succeeded = await secondClient.SendAsync(retry, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, succeeded.StatusCode);
        Assert.Equal(2, second.Sqs.ReceivedCalls().Count());
        var replay = Assert.IsType<ReceiveMessageRequest>(second.Sqs.ReceivedCalls().First().GetArguments()[0]);
        Assert.Equal("11111111-1111-1111-1111-111111111111", replay.ReceiveRequestAttemptId);
        Assert.Equal(1, replay.MaxNumberOfMessages);
        Assert.Equal(0, replay.WaitTimeSeconds);
        Assert.Equal(120, replay.VisibilityTimeout);
        Assert.Single(second.Store.ReceivedCalls());
        await AssertPrivacy(first, failed, token);
        await AssertPrivacy(second, succeeded, token);
    }

    [Fact]
    public async Task WhenDependenciesTogetherExceedBudget_ShouldNotDeleteLateAbandonment()
    {
        await using var factory = new DiscardApplicationFactory(
            new() { ["CommandDlqAdministration:DependencyTimeoutSeconds"] = "1" }
        );
        using var client = factory.CreateClient();
        var token = Selection(factory);
        factory
            .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(600, call.ArgAt<CancellationToken>(1));
                return new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.OK, Messages = [Message()] };
            });
        factory
            .Store.RecordAbandonment(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Delay(600, TestContext.Current.CancellationToken);
                return AbandonmentResult.Recorded;
            });
        using var request = Request(token);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(factory.Sqs.ReceivedCalls());
        Assert.Single(factory.Store.ReceivedCalls());
        await AssertPrivacy(factory, response, token);
    }

    [Fact]
    public async Task WhenReadinessHasNotCompleted_ShouldTimeoutWithoutReplayThenSucceedAfterReadiness()
    {
        await using var factory = new DiscardApplicationFactory(
            new() { ["CommandDlqAdministration:DependencyTimeoutSeconds"] = "1" },
            ready: false
        );
        using var client = factory.CreateClient();
        var token = Selection(factory);
        using var blockedRequest = Request(token);
        using var blocked = await client.SendAsync(blockedRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.Empty(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
        factory.Readiness.MarkCompleted();
        using var readyRequest = Request(token);

        using var succeeded = await client.SendAsync(readyRequest, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, succeeded.StatusCode);
        Assert.Equal(2, factory.Sqs.ReceivedCalls().Count());
        Assert.Single(factory.Store.ReceivedCalls());
        await AssertPrivacy(factory, blocked, token);
        await AssertPrivacy(factory, succeeded, token);
    }

    private static string Selection(
        DiscardApplicationFactory factory,
        Message? message = null,
        DateTimeOffset? expires = null
    )
    {
        message ??= Message();
        var command = NotificationCommandMessageReader.Read(message);
        var digest = factory.Services.GetRequiredService<INotificationCommandDigest>();

        return factory
            .Services.GetRequiredService<CommandDlqSelectionTokens>()
            .Create(
                "11111111-1111-1111-1111-111111111111",
                message.MessageId,
                expires ?? DateTimeOffset.UtcNow.AddSeconds(100),
                digest.CreateImmutableFieldsDigest(command)
            );
    }

    private static string Basic(string client, string secret) =>
        $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{client}:{secret}"))}";

    private static HttpRequestMessage Request(string? token, string? authorization = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { selectionToken = token }),
                Encoding.UTF8,
                "application/json"
            ),
        };
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        else
            request.Headers.TryAddWithoutValidation("Authorization", Basic("admin", Secret));

        return request;
    }

    private static async Task AssertPrivacy(
        DiscardApplicationFactory factory,
        HttpResponseMessage response,
        string? token
    )
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        foreach (
            var value in new[]
            {
                Identity,
                PrivateContent,
                Receipt,
                token,
                Secret,
                "wrong-private-secret",
                "private-token",
                "private-header",
            }.Where(value => !string.IsNullOrEmpty(value))
        )
        {
            Assert.DoesNotContain(value!, text, StringComparison.Ordinal);
            Assert.All(factory.Logs.Messages, log => Assert.DoesNotContain(value!, log, StringComparison.Ordinal));
        }

        Assert.Empty(factory.Notify.ReceivedCalls());
        Assert.DoesNotContain(
            factory.Sqs.ReceivedCalls(),
            call => call.GetMethodInfo().Name == nameof(IAmazonSQS.SendMessageAsync)
        );
    }

    private static Message Message() =>
        new()
        {
            MessageId = "opaque-message-id",
            ReceiptHandle = Receipt,
            Body = JsonSerializer.Serialize(
                new NotificationCommand(
                    1,
                    Identity,
                    new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
                    Identity,
                    $" {Identity.ToUpperInvariant()} ",
                    PrivateContent,
                    JsonSerializer.SerializeToElement(new { content = PrivateContent })
                ),
                s_indentedJsonOptions
            ),
        };

    private sealed class DiscardApplicationFactory(Dictionary<string, string?>? overrides = null, bool ready = true)
        : WebApplicationFactory<Program>
    {
        public IAmazonSQS Sqs { get; } = CreateSqs();
        public INotificationDeliveryRecordStore Store { get; } = CreateStore();
        public INotifyEmailClient Notify { get; } = Substitute.For<INotifyEmailClient>();
        public RecordingLogs Logs { get; } = new();
        public MongoMigrationReadiness Readiness { get; } = new();

        private static IAmazonSQS CreateSqs()
        {
            var sqs = Substitute.For<IAmazonSQS>();
            sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.OK, Messages = [Message()] });

            sqs.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(new SendMessageResponse { HttpStatusCode = HttpStatusCode.OK, MessageId = "destination-id" });
            sqs.DeleteMessageAsync(Arg.Any<DeleteMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(new DeleteMessageResponse { HttpStatusCode = HttpStatusCode.OK });

            return sqs;
        }

        private static INotificationDeliveryRecordStore CreateStore()
        {
            var store = Substitute.For<INotificationDeliveryRecordStore>();
            store
                .RecordAbandonment(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
                .Returns(AbandonmentResult.Recorded);
            store
                .Inspect(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
                .Returns(new NotificationDeliveryState("unrecorded", null, null));

            return store;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILoggerFactory>(_ =>
                    LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs))
                );
                foreach (
                    var descriptor in services
                        .Where(descriptor =>
                            descriptor.ServiceType == typeof(IHostedService)
                            && (
                                descriptor.ImplementationType == typeof(NotificationCommandConsumer)
                                || descriptor.ImplementationType == typeof(MongoMigrationService)
                            )
                        )
                        .ToArray()
                )
                    services.Remove(descriptor);
                services.RemoveAll<IAmazonSQS>();
                services.RemoveAll<INotificationDeliveryRecordStore>();
                services.AddSingleton(Sqs);
                services.AddSingleton(Store);
                services.RemoveAll<INotifyEmailClient>();
                services.AddSingleton(Notify);
                if (ready)
                    Readiness.MarkCompleted();
                services.AddSingleton(Readiness);
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var values = new Dictionary<string, string?>
            {
                ["AWS_EMF_ENABLED"] = "false",
                ["AnalyticsEventConsumer:ProcessingEnabled"] = "false",
                ["NotificationCommandDelivery:ProcessingEnabled"] = "false",
                ["NotificationCommandDelivery:QueueUrl"] = "http://sqs.local/commands.fifo",
                ["NotificationCommandDelivery:EvidenceDigestSecret"] = "private-evidence-secret",
                ["NotificationCommandDelivery:RecipientLaneSecret"] = "private-lane-secret",
                ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = "set-automatically-when-deployed",
                ["NotificationCommandDelivery:NotifyTimeoutSeconds"] = "0",
                ["NotificationCommandDelivery:ReceiveTimeoutSeconds"] = "0",
                ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
                ["Mongo:DatabaseName"] = "inspection-test",
                ["Notify:ApiKey"] = "set-automatically-when-deployed",
                ["Notify:BaseAddress"] = "set-automatically-when-deployed",
                ["CommandDlqAdministration:Enabled"] = "true",
                ["CommandDlqAdministration:QueueUrl"] = "http://sqs.local/commands-dlq.fifo",
                ["Acl:Clients:admin:Type"] = "ApiKey",
                ["Acl:Clients:admin:Secret"] = Secret,
                ["Acl:Clients:admin:Scopes:0"] = "admin",
                ["Acl:Clients:oauth:Type"] = "OAuth",
                ["Acl:Clients:oauth:Secret"] = Secret,
                ["Acl:Clients:oauth:Scopes:0"] = "admin",
                ["Acl:Clients:read:Type"] = "ApiKey",
                ["Acl:Clients:read:Secret"] = Secret,
                ["Acl:Clients:read:Scopes:0"] = "read",
                ["Acl:Clients:write:Type"] = "ApiKey",
                ["Acl:Clients:write:Secret"] = Secret,
                ["Acl:Clients:write:Scopes:0"] = "write",
            };
            foreach (var entry in overrides ?? [])
                values[entry.Key] = entry.Value;
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(values));

            return base.CreateHost(builder);
        }
    }

    private sealed class RecordingLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);

        public void Dispose() { }

        private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) => messages.Enqueue($"{formatter(state, exception)} {exception}");
        }
    }
}
