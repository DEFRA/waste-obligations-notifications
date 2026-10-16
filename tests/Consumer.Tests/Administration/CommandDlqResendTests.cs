using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Administration;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Startup;
using Defra.WasteObligations.Consumer.Tests.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests.Administration;

public sealed class CommandDlqResendTests
{
    private const string Route = "/admin/notification-commands/dlq/resend";
    private const string Parent = "v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task WhenAcceptedSourceIsSelected_ShouldConfirmReplacementBeforeDeletingAndReturnEncodedLinks()
    {
        await using var factory = new ResendApplicationFactory();
        using var client = factory.CreateClient();
        var selected = Selection(factory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmed = new TaskCompletionSource<SendMessageResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        factory
            .Sqs.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                entered.SetResult();

                return confirmed.Task.WaitAsync(call.ArgAt<CancellationToken>(1));
            });
        using var request = Request(selected);
        var pending = client.SendAsync(request, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(pending.IsCompleted);
            Assert.DoesNotContain(
                factory.Sqs.ReceivedCalls(),
                call => call.GetMethodInfo().Name == nameof(IAmazonSQS.DeleteMessageAsync)
            );
        }
        finally
        {
            confirmed.TrySetResult(
                new SendMessageResponse { HttpStatusCode = HttpStatusCode.OK, MessageId = "replacement-copy" }
            );
        }
        using var response = await pending;
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        var send = Sent(factory);
        var replacement = JsonSerializer.Deserialize<NotificationCommand>(send.MessageBody, s_json)!;
        var source = Command();

        // Independent HMAC-SHA256 vectors for the fixture's evidence secret and source-intention key.
        const string sourceKey = "v1:e33ba528eafbf3e486775ef0ec83fa5ba46c3b33e497449a02350db65302b3d8";
        const string replacementKey = "v1:23e2e9431616823e2925d46c55ca429d28a659a579ac24eeba0728eb7896711c";
        const string replacementIdempotencyKey = "resend:" + sourceKey;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(source.IdempotencyKey, replacement.IdempotencyKey);
        Assert.Equal(replacementIdempotencyKey, replacement.IdempotencyKey);
        Assert.Equal(sourceKey, replacement.ResendOfNotificationKey);
        Assert.Equal(source.Personalisation.GetRawText(), replacement.Personalisation.GetRawText());
        Assert.Equal(
            source with
            {
                IdempotencyKey = replacement.IdempotencyKey,
                ResendOfNotificationKey = sourceKey,
                Personalisation = replacement.Personalisation,
            },
            replacement
        );
        Assert.Equal("http://sqs.local/commands.fifo", send.QueueUrl);
        Assert.Equal(factory.Digest.CreateRecipientLane(source.EmailAddress), send.MessageGroupId);
        Assert.Equal(replacementIdempotencyKey, send.MessageDeduplicationId);
        var deleted = Assert.IsType<DeleteMessageRequest>(factory.Sqs.ReceivedCalls().Last().GetArguments()[0]);
        Assert.Equal("selected-receipt", deleted.ReceiptHandle);
        Assert.Equal("http://sqs.local/commands-dlq.fifo", deleted.QueueUrl);
        Assert.Equal(sourceKey, body.RootElement.GetProperty("sourceNotificationKey").GetString());
        Assert.Equal(replacementKey, body.RootElement.GetProperty("replacementNotificationKey").GetString());
        Assert.Equal("confirmed", body.RootElement.GetProperty("publicationStatus").GetString());
        Assert.True(body.RootElement.GetProperty("sourceMessageRemoved").GetBoolean());
        Assert.Equal(
            "/admin/notification-commands/delivery-records?notificationKey=v1%3A" + sourceKey[3..],
            body.RootElement.GetProperty("links").GetProperty("sourceRecord").GetString()
        );
        Assert.Equal(
            "/admin/notification-commands/delivery-records?notificationKey=v1%3A" + replacementKey[3..],
            body.RootElement.GetProperty("links").GetProperty("replacementRecord").GetString()
        );
        Assert.Single(factory.Store.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());

        await response.VerifyResponse();
    }

    [Theory]
    [InlineData("redrive", false)]
    [InlineData("redrive", true)]
    [InlineData("discard", false)]
    [InlineData("discard", true)]
    public async Task WhenIdentityBoundSelectionIsUsedForAnotherAction_ShouldRejectChangedIdentityOrParent(
        string action,
        bool changedParent
    )
    {
        await using var factory = new ResendApplicationFactory();
        using var client = factory.CreateClient();
        var selection = Selection(factory);
        var changed = changedParent
            ? Command() with
            {
                ResendOfNotificationKey = Parent,
            }
            : Command() with
            {
                IdempotencyKey = "another-intention",
            };
        factory
            .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.OK, Messages = [Message(changed)] });
        using var request = Request(selection);
        request.RequestUri = new Uri("/admin/notification-commands/dlq/" + action, UriKind.Relative);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
    }

    [Theory]
    [InlineData("unrecorded")]
    [InlineData("active-claim")]
    [InlineData("expired-claim")]
    [InlineData("delivery-suppressed")]
    [InlineData("delivery-abandoned")]
    [InlineData("immutable-conflict")]
    [InlineData("unknown-delivery-state")]
    public async Task WhenCurrentEvidenceIsIneligible_ShouldRetainSourceWithoutPublication(string classification)
    {
        await using var factory = new ResendApplicationFactory();
        using var client = factory.CreateClient();
        factory
            .Store.Inspect(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(new NotificationDeliveryState(classification, null, null));
        using var request = Request(Selection(factory));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(factory.Sqs.ReceivedCalls());
        Assert.Single(factory.Store.ReceivedCalls());
    }

    [Fact]
    public async Task WhenPublicationConfirmsAfterSelectionExpires_ShouldRetainSourceDespitePossiblePublication()
    {
        await using var factory = new ResendApplicationFactory();
        using var client = factory.CreateClient();
        var selection = Selection(factory);
        factory
            .Sqs.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                factory.Clock.Now = factory.Clock.Now.AddMinutes(3);

                return new SendMessageResponse { HttpStatusCode = HttpStatusCode.OK, MessageId = "possibly-published" };
            });
        using var request = Request(selection);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(
            factory.Sqs.ReceivedCalls(),
            call => call.GetMethodInfo().Name == nameof(IAmazonSQS.DeleteMessageAsync)
        );
    }

    [Theory]
    [InlineData("content")]
    [InlineData("identity")]
    [InlineData("parent")]
    [InlineData("malformed")]
    [InlineData("missing")]
    public async Task WhenReplayDoesNotMatchSelection_ShouldRefuseBeforeInspectingOrPublishing(string condition)
    {
        await using var factory = new ResendApplicationFactory();
        using var client = factory.CreateClient();
        var selected = Selection(factory);
        var command = condition switch
        {
            "content" => Command() with { TemplateId = "changed-template" },
            "identity" => Command() with { IdempotencyKey = "another-intention" },
            "parent" => Command() with { ResendOfNotificationKey = Parent },
            _ => Command(),
        };
        var message = Message(command);
        if (condition == "malformed")
            message.Body = "private-invalid-json";
        factory
            .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReceiveMessageResponse
                {
                    HttpStatusCode = HttpStatusCode.OK,
                    Messages = condition == "missing" ? [] : [message],
                }
            );
        using var request = Request(selected);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("expired")]
    [InlineData("batch")]
    [InlineData("tampered")]
    public async Task WhenSelectionIsNotOneCurrentIdentityBoundToken_ShouldRejectBeforeDependencies(string condition)
    {
        await using var factory = new ResendApplicationFactory();
        using var client = factory.CreateClient();
        var selection = Selection(factory, legacy: condition == "legacy");
        if (condition == "expired")
            factory.Clock.Now = factory.Clock.Now.AddMinutes(3);
        if (condition == "tampered")
            selection += "x";
        using var request = Request(selection, batch: condition == "batch");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("publish-no-id")]
    [InlineData("delete")]
    public async Task WhenPublicationOrDeletionIsUnconfirmed_ShouldReuseReplacementOnFreshCopyAndHost(string stage)
    {
        await using var first = new ResendApplicationFactory();
        using var firstClient = first.CreateClient();
        if (stage.StartsWith("publish", StringComparison.Ordinal))
            first
                .Sqs.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(
                    new SendMessageResponse
                    {
                        HttpStatusCode = stage == "publish" ? HttpStatusCode.BadGateway : HttpStatusCode.OK,
                    }
                );
        else
            first
                .Sqs.DeleteMessageAsync(Arg.Any<DeleteMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(new DeleteMessageResponse { HttpStatusCode = HttpStatusCode.BadGateway });
        using var request = Request(Selection(first));
        using var failed = await firstClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        if (stage.StartsWith("publish", StringComparison.Ordinal))
            Assert.DoesNotContain(
                first.Sqs.ReceivedCalls(),
                call => call.GetMethodInfo().Name == nameof(IAmazonSQS.DeleteMessageAsync)
            );
        var firstSend = Sent(first);
        await using var second = new ResendApplicationFactory();
        using var secondClient = second.CreateClient();
        var copy = Message(Command(), "another-copy");
        second
            .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.OK, Messages = [copy] });
        var fresh = Selection(second, messageId: copy.MessageId);
        using var retry = Request(fresh);
        using var succeeded = await secondClient.SendAsync(retry, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, succeeded.StatusCode);
        Assert.Equal(firstSend.MessageBody, Sent(second).MessageBody);
        Assert.Equal(firstSend.MessageDeduplicationId, Sent(second).MessageDeduplicationId);
        second.Sqs.ClearReceivedCalls();
        using var repeated = Request(fresh);
        using var repeatedResponse = await secondClient.SendAsync(repeated, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, repeatedResponse.StatusCode);
        Assert.Equal(firstSend.MessageBody, Sent(second).MessageBody);
        second.Sqs.ClearReceivedCalls();
        second
            .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.OK, Messages = [] });
        using var gone = Request(fresh);
        using var unavailable = await secondClient.SendAsync(gone, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
        Assert.Single(second.Sqs.ReceivedCalls());
    }

    [Fact]
    public async Task WhenAcceptedReplacementIsSelected_ShouldTreatItAsDistinctImmediateSource()
    {
        await using var factory = new ResendApplicationFactory();
        using var client = factory.CreateClient();
        var command = Command() with { IdempotencyKey = "replacement-source", ResendOfNotificationKey = Parent };
        factory
            .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.OK, Messages = [Message(command)] });
        using var request = Request(Selection(factory, command: command));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var next = JsonSerializer.Deserialize<NotificationCommand>(Sent(factory).MessageBody, s_json)!;
        Assert.NotEqual(command.IdempotencyKey, next.IdempotencyKey);
        Assert.NotEqual(Parent, next.ResendOfNotificationKey);
        Assert.Equal(factory.Digest.CreateIdempotencyKeyDigest(command.IdempotencyKey), next.ResendOfNotificationKey);
    }

    private static SendMessageRequest Sent(ResendApplicationFactory factory) =>
        Assert.IsType<SendMessageRequest>(
            Assert
                .Single(
                    factory.Sqs.ReceivedCalls(),
                    call => call.GetMethodInfo().Name == nameof(IAmazonSQS.SendMessageAsync)
                )
                .GetArguments()[0]
        );

    private static NotificationCommand Command() =>
        new(
            1,
            "source-intention",
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            "submitted",
            "recipient@example.com",
            "private-template",
            JsonSerializer.SerializeToElement(new { body = "private-body" })
        );

    private static Message Message(NotificationCommand command, string messageId = "selected-copy") =>
        new()
        {
            MessageId = messageId,
            ReceiptHandle = "selected-receipt",
            Body = JsonSerializer.Serialize(command, s_json),
        };

    private static string Selection(
        ResendApplicationFactory factory,
        bool legacy = false,
        string messageId = "selected-copy",
        NotificationCommand? command = null
    )
    {
        command ??= Command();

        return factory
            .Services.GetRequiredService<CommandDlqSelectionTokens>()
            .Create(
                "11111111-1111-1111-1111-111111111111",
                messageId,
                factory.Clock.Now.AddSeconds(100),
                factory.Digest.CreateImmutableFieldsDigest(command),
                notificationKey: legacy ? null : factory.Digest.CreateIdempotencyKeyDigest(command.IdempotencyKey),
                resendOfNotificationKey: command.ResendOfNotificationKey
            );
    }

    private static HttpRequestMessage Request(string token, bool batch = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(
                batch
                    ? JsonSerializer.Serialize(new { selectionTokens = new[] { token } })
                    : JsonSerializer.Serialize(new { selectionToken = token }),
                Encoding.UTF8,
                "application/json"
            ),
        };
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:private-secret"))
        );

        return request;
    }

    private sealed class ResendApplicationFactory : WebApplicationFactory<Program>
    {
        public IAmazonSQS Sqs { get; } = Substitute.For<IAmazonSQS>();
        public INotificationDeliveryRecordStore Store { get; } = Substitute.For<INotificationDeliveryRecordStore>();
        public INotifyEmailClient Notify { get; } = Substitute.For<INotifyEmailClient>();
        public ControlledClock Clock { get; } = new();
        public INotificationCommandDigest Digest => Services.GetRequiredService<INotificationCommandDigest>();

        public ResendApplicationFactory()
        {
            Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(
                    new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.OK, Messages = [Message(Command())] }
                );
            Sqs.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(
                    new SendMessageResponse { HttpStatusCode = HttpStatusCode.OK, MessageId = "replacement-copy" }
                );
            Sqs.DeleteMessageAsync(Arg.Any<DeleteMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(new DeleteMessageResponse { HttpStatusCode = HttpStatusCode.OK });
            Store
                .Inspect(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
                .Returns(new NotificationDeliveryState("delivery-accepted", null, null));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                foreach (
                    var descriptor in services
                        .Where(item =>
                            item.ServiceType == typeof(IHostedService)
                            && (
                                item.ImplementationType == typeof(NotificationCommandConsumer)
                                || item.ImplementationType == typeof(MongoMigrationService)
                            )
                        )
                        .ToArray()
                )
                    services.Remove(descriptor);
                services.RemoveAll<IAmazonSQS>();
                services.RemoveAll<INotificationDeliveryRecordStore>();
                services.RemoveAll<INotifyEmailClient>();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(Sqs);
                services.AddSingleton(Store);
                services.AddSingleton(Notify);
                services.AddSingleton<TimeProvider>(Clock);
                var startup = new ApplicationStartup();
                startup.MarkStarted();
                var completion = new MongoMigrationCompletion();
                completion.MarkCompleted();
                services.AddSingleton(startup);
                services.AddSingleton(completion);
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AWS_EMF_ENABLED"] = "false",
                        ["AnalyticsEventConsumer:ProcessingEnabled"] = "false",
                        ["NotificationCommandDelivery:QueueUrl"] = "http://sqs.local/commands.fifo",
                        ["NotificationCommandDelivery:EvidenceDigestSecret"] = "private-evidence-secret",
                        ["NotificationCommandDelivery:RecipientLaneSecret"] = "private-lane-secret",
                        ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
                        ["Mongo:DatabaseName"] = "resend-test",
                        ["Notify:ApiKey"] = NotifyTestCredentials.ApiKey,
                        ["Notify:BaseAddress"] = "http://notify.local",
                        ["CommandDlqAdministration:QueueUrl"] = "http://sqs.local/commands-dlq.fifo",
                        ["Acl:Clients:admin:Type"] = "ApiKey",
                        ["Acl:Clients:admin:Secret"] = "private-secret",
                        ["Acl:Clients:admin:Scopes:0"] = "admin",
                    }
                )
            );

            return base.CreateHost(builder);
        }
    }

    private sealed class ControlledClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
