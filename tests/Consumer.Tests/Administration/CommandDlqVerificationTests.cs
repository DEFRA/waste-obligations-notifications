using System.Net;
using System.Net.Http.Json;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Administration;
using Defra.WasteObligations.Consumer.Authentication;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Endpoints.Admin;
using Defra.WasteObligations.Consumer.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests.Administration;

public sealed class CommandDlqVerificationTests
{
    private const string Route = "/admin/notification-commands/dlq/verification-command";
    private const string PrivateError = "private-recipient@example.com";

    [Theory]
    [InlineData("missing", true, true, 401)]
    [InlineData("viewer", true, true, 403)]
    [InlineData("admin", false, true, 503)]
    [InlineData("admin", true, false, 409)]
    public async Task WhenCreationIsNotPermitted_ShouldRejectBeforeQueueOrStore(
        string caller,
        bool ready,
        bool processing,
        int status
    )
    {
        await using var fixture = await VerificationFixture.Start(ready, processing);
        using var request = Request(caller);
        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Empty(fixture.Store.ReceivedCalls());
        Assert.Empty(fixture.Sqs.ReceivedCalls());
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData(" ", false)]
    [InlineData("{\"emailAddress\":\"private@example.com\"}", false)]
    [InlineData("{}", true)]
    public async Task WhenARequestBodyIsPresent_ShouldRejectIncludingChunkedBodies(string body, bool chunked)
    {
        await using var fixture = await VerificationFixture.Start();
        using var request = Request();
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (chunked)
            request.Headers.TransferEncodingChunked = true;
        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fixture.Store.ReceivedCalls());
        Assert.Empty(fixture.Sqs.ReceivedCalls());
    }

    [Theory]
    [InlineData(SuppressionClaimResult.TerminalDuplicate)]
    [InlineData(SuppressionClaimResult.Conflict)]
    [InlineData(SuppressionClaimResult.ActiveClaim)]
    public async Task WhenSuppressionIsNotFreshlyRecorded_ShouldNotPublish(SuppressionClaimResult result)
    {
        await using var fixture = await VerificationFixture.Start();
        fixture.Store.RecordSuppression(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>()).Returns(result);
        using var request = Request();
        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(fixture.Sqs.ReceivedCalls());
    }

    [Fact]
    public async Task WhenSuppressionIsPending_ShouldPublishOnlyAfterConfirmationWithValidCanonicalFields()
    {
        await using var fixture = await VerificationFixture.Start();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmation = new TaskCompletionSource<SuppressionClaimResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        NotificationCommand? recorded = null;
        fixture
            .Store.RecordSuppression(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                recorded = call.Arg<NotificationCommand>();
                entered.TrySetResult();
                return confirmation.Task.WaitAsync(call.Arg<CancellationToken>());
            });
        using var request = Request();
        var creating = fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Sqs.ReceivedCalls());
        Assert.False(creating.IsCompleted);
        confirmation.TrySetResult(SuppressionClaimResult.Recorded);
        using var response = await creating;
        var result = await response.Content.ReadFromJsonAsync<CommandDlqVerificationCommand>(
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(recorded);
        recorded.Validate();
        Assert.StartsWith("verification-", recorded.IdempotencyKey);
        Assert.Equal(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), recorded.ActionOccurredAtUtc);
        Assert.Equal("admin-verification", recorded.NotificationType);
        Assert.Equal("verification@example.invalid", recorded.EmailAddress);
        Assert.Equal("00000000-0000-0000-0000-000000000000", recorded.TemplateId);
        Assert.Equal("{}", recorded.Personalisation.GetRawText());
        Assert.Equal(recorded.IdempotencyKey, result!.IdempotencyKey);
        Assert.Equal("confirmed-message", result.MessageId);
        var send = (SendMessageRequest)Assert.Single(fixture.Sqs.ReceivedCalls()).GetArguments()[0]!;
        Assert.Equal("http://sqs.local/verification-dlq.fifo", send.QueueUrl);
        Assert.Equal(recorded.IdempotencyKey, send.MessageDeduplicationId);
        Assert.Equal(fixture.Digest.CreateRecipientLane(recorded.EmailAddress), send.MessageGroupId);
        var wire = NotificationCommandMessageReader.Read(new Message { Body = send.MessageBody });
        Assert.Equal(
            fixture.Digest.CreateImmutableFieldsDigest(recorded),
            fixture.Digest.CreateImmutableFieldsDigest(wire)
        );
    }

    [Fact]
    public async Task WhenCreationIsRepeatedWithEmptyBodies_ShouldGenerateDifferentCommandIdentities()
    {
        await using var fixture = await VerificationFixture.Start();
        using var firstRequest = Request();
        firstRequest.Content = new ByteArrayContent([]);
        using var first = await fixture.Client.SendAsync(firstRequest, TestContext.Current.CancellationToken);
        using var secondRequest = Request();
        using var second = await fixture.Client.SendAsync(secondRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstCommand = await first.Content.ReadFromJsonAsync<CommandDlqVerificationCommand>(
            TestContext.Current.CancellationToken
        );
        var secondCommand = await second.Content.ReadFromJsonAsync<CommandDlqVerificationCommand>(
            TestContext.Current.CancellationToken
        );

        Assert.NotEqual(firstCommand!.IdempotencyKey, secondCommand!.IdempotencyKey);
        Assert.Equal(2, fixture.Store.ReceivedCalls().Count());
        Assert.Equal(2, fixture.Sqs.ReceivedCalls().Count());
    }

    [Theory]
    [InlineData("store-throws")]
    [InlineData("store-cancelled")]
    [InlineData("store-late")]
    [InlineData("send-throws")]
    [InlineData("send-late")]
    [InlineData("send-cancelled")]
    [InlineData("send-status")]
    [InlineData("send-missing-id")]
    [InlineData("send-long-id")]
    public async Task WhenADependencyFailsOrIsLate_ShouldReturnFixedFailureAndKeepSuppressionBeforePublication(
        string failure
    )
    {
        await using var fixture = await VerificationFixture.Start();
        if (failure == "store-throws")
            fixture
                .Store.RecordSuppression(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
                .Returns<SuppressionClaimResult>(_ => throw new InvalidOperationException(PrivateError));
        if (failure == "store-cancelled")
            fixture
                .Store.RecordSuppression(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                    return SuppressionClaimResult.Recorded;
                });
        if (failure == "store-late")
            fixture
                .Store.RecordSuppression(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    await Task.Delay(1100, TestContext.Current.CancellationToken);
                    return SuppressionClaimResult.Recorded;
                });
        if (failure.StartsWith("send-", StringComparison.Ordinal))
            fixture
                .Sqs.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    if (failure == "send-cancelled")
                        await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                    if (failure == "send-throws")
                        throw new InvalidOperationException(PrivateError);
                    if (failure == "send-late")
                        await Task.Delay(1100, TestContext.Current.CancellationToken);

                    return new SendMessageResponse
                    {
                        HttpStatusCode = failure == "send-status" ? HttpStatusCode.BadRequest : HttpStatusCode.OK,
                        MessageId = failure switch
                        {
                            "send-missing-id" => null,
                            "send-long-id" => new string('x', 101),
                            _ => "confirmed-message",
                        },
                    };
                });
        using var request = Request();
        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Verification command creation failed.", body, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateError, body, StringComparison.Ordinal);
        Assert.Single(fixture.Store.ReceivedCalls());
        if (failure.StartsWith("store-", StringComparison.Ordinal))
            Assert.Empty(fixture.Sqs.ReceivedCalls());
        else
            Assert.Single(fixture.Sqs.ReceivedCalls());
    }

    private static HttpRequestMessage Request(string caller = "admin")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route);
        if (caller != "missing")
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{caller}:unit-only-secret"))
            );

        return request;
    }

    private sealed class VerificationFixture(
        WebApplication app,
        HttpClient client,
        IAmazonSQS sqs,
        INotificationDeliveryRecordStore store,
        INotificationCommandDigest digest
    ) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public IAmazonSQS Sqs { get; } = sqs;
        public INotificationDeliveryRecordStore Store { get; } = store;
        public INotificationCommandDigest Digest { get; } = digest;

        public static async Task<VerificationFixture> Start(bool ready = true, bool processing = true)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["NotificationCommandDelivery:ProcessingEnabled"] = processing.ToString(),
                    ["NotificationCommandDelivery:QueueUrl"] = "http://sqs.local/verification-source.fifo",
                    ["NotificationCommandDelivery:EvidenceDigestSecret"] = "unit-verification-evidence",
                    ["NotificationCommandDelivery:RecipientLaneSecret"] = "unit-verification-lane",
                    ["CommandDlqAdministration:QueueUrl"] = "http://sqs.local/verification-dlq.fifo",
                    ["CommandDlqAdministration:DependencyTimeoutSeconds"] = "1",
                    ["Acl:Clients:admin:Type"] = "ApiKey",
                    ["Acl:Clients:admin:Secret"] = "unit-only-secret",
                    ["Acl:Clients:admin:Scopes:0"] = "admin",
                    ["Acl:Clients:viewer:Type"] = "ApiKey",
                    ["Acl:Clients:viewer:Secret"] = "unit-only-secret",
                    ["Acl:Clients:viewer:Scopes:0"] = "read",
                }
            );
            builder.Services.AddAuthenticationAuthorization(builder.Configuration);
            builder.Services.AddCommandDlqAdministration(builder.Configuration);
            builder.Services.Configure<NotificationCommandDeliveryOptions>(
                builder.Configuration.GetSection(NotificationCommandDeliveryOptions.SectionName)
            );
            builder.Services.AddSingleton<INotificationCommandDigest, NotificationCommandDigest>();
            builder.Services.AddSingleton<
                INotificationDeliveryRecordStoreFactory,
                NotificationDeliveryRecordStoreFactory
            >();
            var startup = new ApplicationStartup();
            if (ready)
                startup.MarkStarted();
            builder.Services.AddSingleton(startup);
            var store = Substitute.For<INotificationDeliveryRecordStore>();
            store
                .RecordSuppression(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
                .Returns(SuppressionClaimResult.Recorded);
            var sqs = Substitute.For<IAmazonSQS>();
            sqs.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(
                    new SendMessageResponse { HttpStatusCode = HttpStatusCode.OK, MessageId = "confirmed-message" }
                );
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton(sqs);
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapAdminEndpoints();
            await app.StartAsync(TestContext.Current.CancellationToken);

            return new(
                app,
                app.GetTestClient(),
                sqs,
                store,
                app.Services.GetRequiredService<INotificationCommandDigest>()
            );
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }
}
