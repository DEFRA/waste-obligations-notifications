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
using Defra.WasteObligations.Consumer.Startup;
using Elastic.CommonSchema.Serilog;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using Serilog.Events;

namespace Defra.WasteObligations.Consumer.Tests.Administration;

[Collection(nameof(ProductionLoggingCollection))]
public sealed class CommandDlqInspectionTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);
    private const string Route = "/admin/notification-commands/dlq/inspect";
    private const string Secret = "private-päss:secret";
    private const string Identity = "private-recipient@example.com";
    private const string PrivateContent = "private-personalisation-template-notify";
    private const string Receipt = "private-receipt-handle";
    private const string DlqArn = "arn:aws:sqs:eu-west-2:000000000000:commands-dlq.fifo";
    private const string CommandArn = "arn:aws:sqs:eu-west-2:000000000000:commands.fifo";

    [Theory]
    [InlineData("missing", 401)]
    [InlineData("malformed", 401)]
    [InlineData("empty-parameter", 401)]
    [InlineData("base64", 401)]
    [InlineData("utf8", 401)]
    [InlineData("multiple", 401)]
    [InlineData("unknown", 401)]
    [InlineData("empty-client", 401)]
    [InlineData("empty-secret", 401)]
    [InlineData("missing-colon", 401)]
    [InlineData("wrong", 401)]
    [InlineData("oauth", 401)]
    [InlineData("bearer", 401)]
    [InlineData("read", 403)]
    [InlineData("write", 403)]
    public async Task WhenCallerIsNotBasicAdmin_ShouldDenyBeforeQueueOrStorage(string condition, int status)
    {
        await using var factory = new InspectionApplicationFactory();
        using var client = factory.CreateClient();
        using var request = Request(condition);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Empty(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
        AssertPrivacy(factory, body);
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("redrive")]
    [InlineData("discard")]
    [InlineData("redrive-all")]
    [InlineData("status")]
    public async Task WhenAclIsEmpty_ShouldStartAndDenyEveryAdminRouteWithoutEffects(string action)
    {
        await using var factory = new InspectionApplicationFactory(emptyAcl: true);
        using var client = factory.CreateClient();
        using var request = Request("admin");
        request.RequestUri = new Uri($"/admin/notification-commands/dlq/{action}", UriKind.Relative);
        if (action == "status")
            request.Method = HttpMethod.Get;
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Empty(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
    }

    [Fact]
    public async Task WhenConfiguredClientsHaveNoAdminScope_ShouldStartAndDenyAdministratorAccess()
    {
        await using var factory = new InspectionApplicationFactory(new() { ["Acl:Clients:admin:Scopes:0"] = "read" });
        using var client = factory.CreateClient();
        using var request = Request("admin");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
    }

    [Theory]
    [InlineData("Acl:Clients:admin:Type", "private-unknown-type")]
    [InlineData("Acl:Clients:admin:Secret", "set-automatically-when-deployed")]
    [InlineData("Acl:Clients:read:Type", "private-malformed-entry")]
    [InlineData("CommandDlqAdministration:QueueUrl", "private-invalid-queue")]
    [InlineData("CommandDlqAdministration:QueueUrl", "http://sqs.local/commands.fifo")]
    [InlineData("CommandDlqAdministration:QueueUrl", "HTTP://SQS.LOCAL:80/commands.fifo?ignored=query#fragment")]
    [InlineData("NotificationCommandDelivery:QueueUrl", "set-automatically-when-deployed")]
    [InlineData("CommandDlqAdministration:SelectionLifetimeSeconds", "300")]
    [InlineData("CommandDlqAdministration:DependencyTimeoutSeconds", "120")]
    [InlineData("CommandDlqAdministration:DependencyTimeoutSeconds", "private-invalid-duration")]
    [InlineData("NotificationCommandDelivery:EvidenceDigestSecret", "set-automatically-when-deployed")]
    [InlineData("NotificationCommandDelivery:RecipientLaneSecret", "set-automatically-when-deployed")]
    public async Task WhenEnabledConfigurationIsInvalid_ShouldFailBeforeDependenciesWithoutExposingValues(
        string field,
        string value
    )
    {
        await using var factory = new InspectionApplicationFactory(new() { [field] = value });

        var exception = Record.Exception(() =>
        {
            using var client = factory.CreateClient();
        });

        Assert.NotNull(exception);
        Assert.Empty(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
        AssertPrivacy(factory, exception.ToString());
        if (value.StartsWith("private-", StringComparison.Ordinal))
            Assert.DoesNotContain(value, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenAdminInspectsValidCommand_ShouldReturnEmailInputsAndContentFreeSelection()
    {
        await using var factory = new InspectionApplicationFactory();
        using var client = factory.CreateClient();
        using var request = Request("admin");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(text);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Identity, body.RootElement.GetProperty("messages")[0].GetProperty("idempotencyKey").GetString());
        Assert.Equal(Identity, body.RootElement.GetProperty("messages")[0].GetProperty("notificationType").GetString());
        Assert.Equal(
            "unrecorded",
            body.RootElement.GetProperty("messages")[0].GetProperty("failureClassification").GetString()
        );
        Assert.False(body.RootElement.GetProperty("messages")[0].TryGetProperty("failureDetails", out _));
        Assert.Equal(3, body.RootElement.GetProperty("messages")[0].GetProperty("receiveCount").GetInt32());
        Assert.Equal(
            "2026-10-01T00:00:00+00:00",
            body.RootElement.GetProperty("messages")[0].GetProperty("actionOccurredAtUtc").GetString()
        );
        Assert.Equal(
            "2026-10-01T00:00:00+00:00",
            body.RootElement.GetProperty("messages")[0].GetProperty("sentAtUtc").GetString()
        );
        Assert.StartsWith(
            "v1:",
            body.RootElement.GetProperty("messages")[0].GetProperty("recipientDigest").GetString()
        );
        Assert.Equal(
            [
                "actionOccurredAtUtc",
                "availableActions",
                "deliveryRecord",
                "emailAddress",
                "failureClassification",
                "idempotencyKey",
                "leaseExpiresAtUtc",
                "links",
                "messageId",
                "notificationType",
                "notifyNotificationId",
                "notifyReference",
                "operatorGuidance",
                "personalisation",
                "receiveCount",
                "recipientDigest",
                "recordedAtUtc",
                "schemaVersion",
                "selectionToken",
                "sentAtUtc",
                "templateId",
            ],
            body.RootElement.GetProperty("messages")[0].EnumerateObject().Select(property => property.Name).Order()
        );
        var token = body.RootElement.GetProperty("messages")[0].GetProperty("selectionToken").GetString()!;
        Assert.StartsWith("v4.", token);
        using var payload = JsonDocument.Parse(DecodePayload(token));
        Assert.Equal(
            [
                "expiresAtUtc",
                "immutableFieldsDigest",
                "maxNumberOfMessages",
                "messageId",
                "notificationKey",
                "queueBinding",
                "receiveRequestAttemptId",
                "visibilityTimeoutSeconds",
            ],
            payload.RootElement.EnumerateObject().Select(property => property.Name).Order()
        );
        Assert.Equal("opaque-message-id", payload.RootElement.GetProperty("messageId").GetString());
        AssertPrivacy(factory, payload.RootElement.GetRawText());
        Assert.Equal(Identity, body.RootElement.GetProperty("messages")[0].GetProperty("emailAddress").GetString());
        Assert.Equal(PrivateContent, body.RootElement.GetProperty("messages")[0].GetProperty("templateId").GetString());
        Assert.Equal(
            PrivateContent,
            body.RootElement.GetProperty("messages")[0]
                .GetProperty("personalisation")
                .GetProperty("content")
                .GetString()
        );
        Assert.DoesNotContain(Receipt, text, StringComparison.Ordinal);
        await factory
            .Sqs.Received(1)
            .ReceiveMessageAsync(
                Arg.Is<ReceiveMessageRequest>(receive =>
                    receive.MaxNumberOfMessages == 10
                    && receive.WaitTimeSeconds == 0
                    && receive.VisibilityTimeout == 120
                    && receive.MessageSystemAttributeNames.Contains("ApproximateReceiveCount")
                    && receive.MessageSystemAttributeNames.Contains("SentTimestamp")
                ),
                Arg.Any<CancellationToken>()
            );
        await factory
            .Store.Received(1)
            .Inspect(
                Arg.Is<NotificationCommand>(command =>
                    command.IdempotencyKey == Identity && command.EmailAddress == Identity
                ),
                Arg.Any<CancellationToken>()
            );
        Assert.Single(factory.Store.ReceivedCalls());
        Assert.Single(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
        Assert.Contains(
            factory.Logs.Messages,
            message => message.Contains("unrecorded for other", StringComparison.Ordinal)
        );
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("unsupported")]
    public async Task WhenCommandIsMalformedOrUnsupported_ShouldReturnFixedClassificationWithoutPartialIdentityOrSelection(
        string condition
    )
    {
        await using var factory = new InspectionApplicationFactory();
        var message = Message();
        message.Body =
            condition == "malformed"
                ? $"{{\"idempotencyKey\":\"{Identity}\",\"emailAddress\":\"{Identity}\",private"
                : message.Body.Replace("\"schemaVersion\":1", "\"schemaVersion\":99", StringComparison.Ordinal);
        factory
            .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.OK, Messages = [message] });
        using var client = factory.CreateClient();
        using var request = Request("admin");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(text);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "invalid-or-unsupported-command",
            body.RootElement.GetProperty("messages")[0].GetProperty("failureClassification").GetString()
        );
        foreach (
            var name in new[]
            {
                "idempotencyKey",
                "notificationType",
                "actionOccurredAtUtc",
                "recipientDigest",
                "recordedAtUtc",
                "leaseExpiresAtUtc",
                "selectionToken",
                "emailAddress",
                "templateId",
                "personalisation",
                "schemaVersion",
                "deliveryRecord",
                "notifyNotificationId",
                "notifyReference",
                "links",
            }
        )
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("messages")[0].GetProperty(name).ValueKind);
        Assert.Empty(body.RootElement.GetProperty("messages")[0].GetProperty("availableActions").EnumerateArray());
        Assert.Contains(
            "Escalate to engineering",
            body.RootElement.GetProperty("messages")[0].GetProperty("operatorGuidance").GetString()!,
            StringComparison.Ordinal
        );
        Assert.Empty(factory.Store.ReceivedCalls());
        AssertPrivacy(factory, text);
    }

    [Theory]
    [InlineData("delivery-accepted", "inspect,redrive,resend", "does not confirm recipient delivery")]
    [InlineData("active-claim", "inspect", "not proof that its process is running")]
    [InlineData("expired-claim", "inspect,redrive,discard", "inconclusive lookup leaves a duplicate-email risk")]
    [InlineData("unrecorded", "redrive,discard", "fix the processing failure")]
    [InlineData("delivery-suppressed", "inspect,redrive", "Ordinary replay cannot undo suppression")]
    [InlineData("delivery-abandoned", "discard", "does not prove an earlier uncertain request failed")]
    [InlineData("immutable-conflict", "", "producer/evidence mismatch")]
    [InlineData("unknown-delivery-state", "", "Escalate to engineering")]
    public async Task WhenInspectingKnownClassification_ShouldOfferOnlyImplementedGuardedActions(
        string classification,
        string actions,
        string instruction
    )
    {
        await using var factory = new InspectionApplicationFactory();
        factory
            .Store.Inspect(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(new NotificationDeliveryState(classification, null, null));
        using var client = factory.CreateClient();
        using var request = Request("admin");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        var item = Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray());
        var guidance = item.GetProperty("operatorGuidance").GetString()!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(instruction, guidance, StringComparison.Ordinal);
        Assert.Contains("current evidence, not a historical failure cause", guidance, StringComparison.Ordinal);
        var available = item.GetProperty("availableActions").EnumerateArray().ToArray();
        Assert.Equal(
            actions
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(action => "/admin/notification-commands/dlq/" + action),
            available.Select(action => action.GetProperty("endpoint").GetString())
        );
        Assert.All(available, action => Assert.Equal("POST", action.GetProperty("method").GetString()));
        Assert.Equal(classification == "delivery-accepted", available.Any(action => action.GetProperty("endpoint").GetString()!.EndsWith("/resend", StringComparison.Ordinal)));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("deliveryRecord").ValueKind);
        Assert.Single(factory.Store.ReceivedCalls());
        Assert.Single(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
    }

    [Theory]
    [InlineData("Notify rejection", "expired-claim", "If rejection is confirmed, fix the cause", "credentials")]
    [InlineData(
        "lost response",
        "expired-claim",
        "Check Notify using the original reference",
        "If Notify confirms delivery, discard"
    )]
    [InlineData(
        "accepted then delivery failed",
        "delivery-accepted",
        "the record remains accepted",
        "explicit resend requests the one stable replacement"
    )]
    public async Task WhenInvestigatingAgreedFailureScenario_ShouldGiveConditionalRecoveryWithoutInventingTheCause(
        string scenario,
        string classification,
        string instruction,
        string qualification
    )
    {
        await using var factory = new InspectionApplicationFactory();
        factory
            .Store.Inspect(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(new NotificationDeliveryState(classification, null, null));
        using var client = factory.CreateClient();
        using var request = Request("admin");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        var item = body.RootElement.GetProperty("messages")[0];
        var guidance = item.GetProperty("operatorGuidance").GetString()!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(instruction, guidance, StringComparison.Ordinal);
        Assert.Contains(qualification, guidance, StringComparison.Ordinal);
        Assert.False(item.TryGetProperty("failureDetails", out _), scenario);
        if (classification == "expired-claim")
        {
            Assert.Contains(
                "pending evidence cannot distinguish Notify rejection, a lost response",
                guidance,
                StringComparison.Ordinal
            );
            Assert.Contains("inconclusive lookup leaves a duplicate-email risk", guidance, StringComparison.Ordinal);
            var reference = factory
                .Services.GetRequiredService<INotificationCommandDigest>()
                .CreateNotifyReference(Identity);
            Assert.Equal(reference, item.GetProperty("notifyReference").GetString());
        }
        else
            Assert.Contains("redrive still performs terminal-copy cleanup only", guidance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenInspectingMatchingAcceptedRecord_ShouldReturnRetainedSummaryAndEncodedNavigation()
    {
        await using var factory = new InspectionApplicationFactory();
        using var client = factory.CreateClient();
        var digest = factory.Services.GetRequiredService<INotificationCommandDigest>();
        var key = digest.CreateIdempotencyKeyDigest(Identity);
        var evidence = new NotificationDeliveryEvidence(
            key,
            "submitted",
            "delivery-accepted",
            new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc),
            null,
            "11111111-1111-1111-1111-111111111111",
            "v1:retained-reference",
            "retained-template",
            3
        );
        factory
            .Store.Inspect(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(
                new NotificationDeliveryState(
                    "delivery-accepted",
                    new DateTimeOffset(evidence.RecordedAtUtc),
                    null,
                    evidence
                )
            );
        using var request = Request("admin");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        var item = body.RootElement.GetProperty("messages")[0];
        var summary = item.GetProperty("deliveryRecord");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(key, summary.GetProperty("notificationKey").GetString());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("resendOfNotificationKey").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("links").ValueKind);
        Assert.Equal("11111111-1111-1111-1111-111111111111", item.GetProperty("notifyNotificationId").GetString());
        Assert.Equal("v1:retained-reference", item.GetProperty("notifyReference").GetString());
        Assert.Equal("v1:retained-reference", summary.GetProperty("notifyReference").GetString());
        Assert.Equal("2026-10-12T09:00:00Z", summary.GetProperty("acceptedAtUtc").GetString());
        Assert.Equal("retained-template", summary.GetProperty("templateId").GetString());
        Assert.Equal(3, summary.GetProperty("templateVersion").GetInt32());
        var encodedKey = "v1%3A" + key[3..];
        Assert.Equal("/admin/notification-commands/delivery-records?resendOfNotificationKey=" + encodedKey, item.GetProperty("links").GetProperty("resends").GetString());
        Assert.Equal(
            "/admin/notification-commands/delivery-records?notificationKey=" + encodedKey,
            item.GetProperty("links").GetProperty("deliveryRecord").GetString()
        );
        foreach (
            var field in new[] { "recipient", "immutableFields", "attemptOwner", "emailAddress", "personalisation" }
        )
            Assert.False(summary.TryGetProperty(field, out _));
        Assert.Single(factory.Store.ReceivedCalls());
        Assert.Single(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
    }

    [Fact]
    public async Task WhenBatchContainsMalformedCommand_ShouldSelectOnlyValidCommandsWithoutMutations()
    {
        await using var factory = new InspectionApplicationFactory();
        var first = Message();
        var invalid = Message();
        invalid.MessageId = "malformed-message";
        invalid.Body = "invalid-json";
        var third = Message();
        third.MessageId = "third-message";
        factory
            .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.OK, Messages = [first, invalid, third] }
            );
        using var client = factory.CreateClient();
        using var request = Request("admin");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        var messages = body.RootElement.GetProperty("messages");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("invalid-or-unsupported-command", messages[1].GetProperty("failureClassification").GetString());
        Assert.Equal(JsonValueKind.Null, messages[1].GetProperty("selectionToken").ValueKind);
        Assert.Equal(JsonValueKind.Null, messages[1].GetProperty("emailAddress").ValueKind);
        Assert.Equal(JsonValueKind.Null, messages[1].GetProperty("personalisation").ValueKind);
        var selections =
            factory.Services.GetRequiredService<Defra.WasteObligations.Consumer.Administration.CommandDlqSelectionTokens>();
        var firstSelection = selections.Validate(messages[0].GetProperty("selectionToken").GetString());
        var thirdSelection = selections.Validate(messages[2].GetProperty("selectionToken").GetString());
        Assert.NotNull(firstSelection);
        Assert.NotNull(thirdSelection);
        Assert.Equal(firstSelection.ReceiveRequestAttemptId, thirdSelection.ReceiveRequestAttemptId);
        Assert.Equal(firstSelection.ExpiresAtUtc, thirdSelection.ExpiresAtUtc);
        Assert.Equal(2, factory.Store.ReceivedCalls().Count());
        Assert.All(
            factory.Store.ReceivedCalls(),
            call => Assert.Equal(nameof(INotificationDeliveryRecordStore.Inspect), call.GetMethodInfo().Name)
        );
        Assert.Single(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
        AssertPrivacy(factory, DecodePayload(messages[0].GetProperty("selectionToken").GetString()!));
    }

    [Fact]
    public async Task WhenDlqIsEmpty_ShouldReturnNoContentWithoutStorageEffects()
    {
        await using var factory = new InspectionApplicationFactory();
        factory
            .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.OK, Messages = [] });
        using var client = factory.CreateClient();
        using var request = Request("admin");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(factory.Store.ReceivedCalls());
    }

    [Fact]
    public async Task WhenAdminOnlyHostUsesMandatoryDeliveryDependencies_ShouldInspectAndRegisterNotifyHealth()
    {
        await using var factory = new InspectionApplicationFactory();
        using var client = factory.CreateClient();
        using var request = Request("admin");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var readiness = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        Assert.Single(factory.Sqs.ReceivedCalls());
        Assert.Single(factory.Store.ReceivedCalls());
        var registrations = factory
            .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>>()
            .Value.Registrations;
        Assert.Contains(registrations, registration => registration.Name == "NotificationCommandQueue");
        Assert.Contains(registrations, registration => registration.Name == "NotificationCommandDeadLetterQueue");
        Assert.Contains(registrations, registration => registration.Name == "NotificationDeliveryRecordStore");
        Assert.Contains(registrations, registration => registration.Name == "Notify");
        Assert.Empty(factory.Notify.ReceivedCalls());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenDependencyFails_ShouldReturnOnlyFixedSafeFailureWithoutMutation(bool storage)
    {
        await using var factory = new InspectionApplicationFactory();
        if (storage)
            factory
                .Store.Inspect(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
                .Returns<Task<NotificationDeliveryState>>(_ =>
                    throw new InvalidOperationException($"{Identity} {PrivateContent} {Receipt}")
                );
        else
            factory
                .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns<Task<ReceiveMessageResponse>>(_ =>
                    throw new InvalidOperationException($"{Identity} {PrivateContent} {Receipt}")
                );
        using var client = factory.CreateClient();
        using var request = Request("admin");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Command DLQ inspection failed.", text, StringComparison.Ordinal);
        AssertPrivacy(factory, text);
        Assert.All(
            factory.Store.ReceivedCalls(),
            call => Assert.Equal(nameof(INotificationDeliveryRecordStore.Inspect), call.GetMethodInfo().Name)
        );
        Assert.All(
            factory.Sqs.ReceivedCalls(),
            call => Assert.Equal(nameof(IAmazonSQS.ReceiveMessageAsync), call.GetMethodInfo().Name)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenDependencyIgnoresCancellationAndConfirmsLate_ShouldRejectWithoutSelection(bool storage)
    {
        await using var factory = new InspectionApplicationFactory(
            new() { ["CommandDlqAdministration:DependencyTimeoutSeconds"] = "1" }
        );
        if (storage)
            factory
                .Store.Inspect(Arg.Any<NotificationCommand>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken);
                    Assert.True(call.Arg<CancellationToken>().IsCancellationRequested);

                    return new NotificationDeliveryState("unrecorded", null, null);
                });
        else
            factory
                .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken);
                    Assert.True(call.Arg<CancellationToken>().IsCancellationRequested);

                    return new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.OK, Messages = [Message()] };
                });
        using var client = factory.CreateClient();
        using var request = Request("admin");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        if (!storage)
            Assert.Empty(factory.Store.ReceivedCalls());
        AssertPrivacy(factory, text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenReceiveDoesNotComplete_ShouldCancelActualDependencyWithoutSelection(bool callerCancellation)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory = new InspectionApplicationFactory(
            new() { ["CommandDlqAdministration:DependencyTimeoutSeconds"] = callerCancellation ? "30" : "1" }
        );
        factory
            .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                started.TrySetResult();

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }

                return new ReceiveMessageResponse();
            });
        using var client = factory.CreateClient();
        using var request = Request("admin");
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sending = client.SendAsync(request, source.Token);

        if (callerCancellation)
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await source.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        }
        else
        {
            using var response = await sending;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            AssertPrivacy(factory, await response.Content.ReadAsStringAsync(source.Token));
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Empty(factory.Store.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
    }

    [Fact]
    public async Task WhenReceiveIsDelayed_ShouldAnchorSelectionExpiryBeforeReceive()
    {
        DateTimeOffset receiveStarted = default;
        await using var factory = new InspectionApplicationFactory();
        factory
            .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                receiveStarted = DateTimeOffset.UtcNow;
                await Task.Delay(TimeSpan.FromMilliseconds(200), call.Arg<CancellationToken>());

                return new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.OK, Messages = [Message()] };
            });
        using var client = factory.CreateClient();
        using var request = Request("admin");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        using var selection = JsonDocument.Parse(
            DecodePayload(body.RootElement.GetProperty("messages")[0].GetProperty("selectionToken").GetString()!)
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(
            selection.RootElement.GetProperty("expiresAtUtc").GetDateTimeOffset() <= receiveStarted.AddSeconds(120)
        );
    }

    [Fact]
    public async Task WhenAdminChecksStatus_ShouldReturnApproximateCountsWithoutReceivingMessages()
    {
        await using var factory = new InspectionApplicationFactory();
        factory
            .Sqs.GetQueueAttributesAsync(Arg.Any<GetQueueAttributesRequest>(), Arg.Any<CancellationToken>())
            .Returns(CountResponse());
        using var client = factory.CreateClient();
        using var request = StatusRequest("admin");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(10, body.RootElement.GetProperty("approximateVisibleMessages").GetInt64());
        Assert.Equal(4, body.RootElement.GetProperty("approximateInFlightMessages").GetInt64());
        Assert.Equal(2, body.RootElement.GetProperty("approximateDelayedMessages").GetInt64());
        Assert.Equal(16, body.RootElement.GetProperty("approximateTotalMessages").GetInt64());
        var calls = factory.Sqs.ReceivedCalls().ToArray();
        Assert.Equal(
            new[] { nameof(IAmazonSQS.GetQueueAttributesAsync), nameof(IAmazonSQS.ListMessageMoveTasksAsync) },
            calls.Select(call => call.GetMethodInfo().Name)
        );
        var call = calls[0];
        var attributes = Assert.IsType<GetQueueAttributesRequest>(call.GetArguments()[0]);
        Assert.Equal("http://sqs.local/commands-dlq.fifo", attributes.QueueUrl);
        Assert.Contains("ApproximateNumberOfMessagesNotVisible", attributes.AttributeNames);
        Assert.Empty(factory.Store.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
        AssertPrivacy(factory, body.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("missing", HttpStatusCode.Unauthorized)]
    [InlineData("read", HttpStatusCode.Forbidden)]
    public async Task WhenStatusCallerIsNotAdmin_ShouldDenyBeforeQueueAccess(string condition, HttpStatusCode status)
    {
        await using var factory = new InspectionApplicationFactory();
        using var client = factory.CreateClient();
        using var request = StatusRequest(condition);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(status, response.StatusCode);
        Assert.Empty(factory.Sqs.ReceivedCalls());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("private-invalid-count")]
    [InlineData("-1")]
    public async Task WhenQueueCountIsMissingOrInvalid_ShouldReturnSafeFailure(string? value)
    {
        await using var factory = new InspectionApplicationFactory();
        var attributes = CountResponse();
        if (value is null)
            attributes.Attributes.Remove("ApproximateNumberOfMessages");
        else
            attributes.Attributes["ApproximateNumberOfMessages"] = value;
        factory
            .Sqs.GetQueueAttributesAsync(Arg.Any<GetQueueAttributesRequest>(), Arg.Any<CancellationToken>())
            .Returns(attributes);
        using var client = factory.CreateClient();
        using var request = StatusRequest("admin");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Command DLQ status failed.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-invalid-count", body, StringComparison.Ordinal);
        Assert.Single(factory.Sqs.ReceivedCalls());
        AssertPrivacy(factory, body);
    }

    [Fact]
    public async Task WhenStatusDependencyConfirmsAfterDeadline_ShouldRejectLateCounts()
    {
        await using var factory = new InspectionApplicationFactory(
            new() { ["CommandDlqAdministration:DependencyTimeoutSeconds"] = "1" }
        );
        factory
            .Sqs.GetQueueAttributesAsync(Arg.Any<GetQueueAttributesRequest>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1100), TestContext.Current.CancellationToken);
                return CountResponse();
            });
        using var client = factory.CreateClient();
        using var request = StatusRequest("admin");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertPrivacy(factory, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenAdminRedrivesAll_ShouldStartAwsTaskForConfiguredQueuesWithoutReceivingContent()
    {
        await using var factory = new InspectionApplicationFactory();
        ConfigureQueueIdentities(factory);
        factory
            .Sqs.StartMessageMoveTaskAsync(Arg.Any<StartMessageMoveTaskRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new StartMessageMoveTaskResponse
                {
                    HttpStatusCode = HttpStatusCode.OK,
                    TaskHandle = "opaque-task-handle",
                }
            );
        using var client = factory.CreateClient();
        using var request = RedriveAllRequest("admin");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("/admin/notification-commands/dlq/status", response.Headers.Location?.OriginalString);
        Assert.Equal("opaque-task-handle", body.RootElement.GetProperty("taskHandle").GetString());
        var calls = factory.Sqs.ReceivedCalls().ToArray();
        Assert.Equal(
            new[]
            {
                nameof(IAmazonSQS.GetQueueAttributesAsync),
                nameof(IAmazonSQS.GetQueueAttributesAsync),
                nameof(IAmazonSQS.StartMessageMoveTaskAsync),
            },
            calls.Select(call => call.GetMethodInfo().Name)
        );
        var task = Assert.IsType<StartMessageMoveTaskRequest>(calls[2].GetArguments()[0]);
        Assert.Equal(DlqArn, task.SourceArn);
        Assert.Equal(CommandArn, task.DestinationArn);
        Assert.Null(task.MaxNumberOfMessagesPerSecond);
        Assert.Empty(factory.Store.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
        AssertPrivacy(factory, body.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("missing", HttpStatusCode.Unauthorized)]
    [InlineData("read", HttpStatusCode.Forbidden)]
    public async Task WhenWholeQueueRedriveCallerIsNotAdmin_ShouldDenyBeforeQueueAccess(
        string condition,
        HttpStatusCode status
    )
    {
        await using var factory = new InspectionApplicationFactory();
        using var client = factory.CreateClient();
        using var request = RedriveAllRequest(condition);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(status, response.StatusCode);
        Assert.Empty(factory.Sqs.ReceivedCalls());
    }

    [Fact]
    public async Task WhenWholeQueueRedriveHasBody_ShouldRejectBeforeStartingTask()
    {
        await using var factory = new InspectionApplicationFactory();
        using var client = factory.CreateClient();
        using var request = RedriveAllRequest("admin");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Sqs.ReceivedCalls());
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("unconfirmed")]
    [InlineData("same-queue")]
    public async Task WhenWholeQueueRedriveCannotBeConfirmed_ShouldReturnSafeFailure(string condition)
    {
        await using var factory = new InspectionApplicationFactory();
        ConfigureQueueIdentities(factory, condition == "same-queue");
        factory
            .Sqs.StartMessageMoveTaskAsync(Arg.Any<StartMessageMoveTaskRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<StartMessageMoveTaskResponse>>(_ =>
                condition == "failure"
                    ? throw new InvalidOperationException($"{Identity} {PrivateContent} {Receipt}")
                    : Task.FromResult(new StartMessageMoveTaskResponse { HttpStatusCode = HttpStatusCode.OK })
            );
        using var client = factory.CreateClient();
        using var request = RedriveAllRequest("admin");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Command DLQ whole-queue redrive failed.", text, StringComparison.Ordinal);
        AssertPrivacy(factory, text);
        Assert.Empty(factory.Store.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
        if (condition == "same-queue")
            Assert.Equal(2, factory.Sqs.ReceivedCalls().Count());
    }

    [Theory]
    [InlineData("RUNNING")]
    [InlineData("COMPLETED")]
    [InlineData("FAILED")]
    public async Task WhenStatusIncludesLatestTask_ShouldReturnProgressWithoutAwsFailureDetails(string status)
    {
        await using var factory = new InspectionApplicationFactory();
        factory
            .Sqs.GetQueueAttributesAsync(Arg.Any<GetQueueAttributesRequest>(), Arg.Any<CancellationToken>())
            .Returns(CountResponse());
        factory
            .Sqs.ListMessageMoveTasksAsync(Arg.Any<ListMessageMoveTasksRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ListMessageMoveTasksResponse
                {
                    HttpStatusCode = HttpStatusCode.OK,
                    Results =
                    [
                        new ListMessageMoveTasksResultEntry
                        {
                            Status = status,
                            ApproximateNumberOfMessagesMoved = 25,
                            ApproximateNumberOfMessagesToMove = 100,
                            FailureReason = PrivateContent,
                            SourceArn = DlqArn,
                            DestinationArn = CommandArn,
                            StartedTimestamp = 1790812800000,
                        },
                    ],
                }
            );
        using var client = factory.CreateClient();
        using var request = StatusRequest("admin");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        var task = body.RootElement.GetProperty("redriveTask");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(status, task.GetProperty("status").GetString());
        Assert.Equal(25, task.GetProperty("approximateMessagesMoved").GetInt64());
        Assert.Equal(100, task.GetProperty("approximateMessagesToMove").GetInt64());
        Assert.Equal("2026-10-01T00:00:00+00:00", task.GetProperty("startedAtUtc").GetString());
        Assert.False(task.TryGetProperty("failureReason", out _));
        var call = factory
            .Sqs.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAmazonSQS.ListMessageMoveTasksAsync));
        var listing = Assert.IsType<ListMessageMoveTasksRequest>(call.GetArguments()[0]);
        Assert.Equal(DlqArn, listing.SourceArn);
        Assert.Equal(1, listing.MaxResults);
        AssertPrivacy(factory, body.RootElement.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenWholeQueueDependencyConfirmsAfterDeadline_ShouldRejectLateEffects(bool taskStarted)
    {
        await using var factory = new InspectionApplicationFactory(
            new() { ["CommandDlqAdministration:DependencyTimeoutSeconds"] = "1" }
        );
        ConfigureQueueIdentities(factory);
        if (!taskStarted)
            factory
                .Sqs.GetQueueAttributesAsync(Arg.Any<GetQueueAttributesRequest>(), Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1100), TestContext.Current.CancellationToken);

                    return new GetQueueAttributesResponse
                    {
                        HttpStatusCode = HttpStatusCode.OK,
                        Attributes = new() { ["QueueArn"] = DlqArn },
                    };
                });
        factory
            .Sqs.StartMessageMoveTaskAsync(Arg.Any<StartMessageMoveTaskRequest>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1100), TestContext.Current.CancellationToken);

                return new StartMessageMoveTaskResponse
                {
                    HttpStatusCode = HttpStatusCode.OK,
                    TaskHandle = "late-task-handle",
                };
            });
        using var client = factory.CreateClient();
        using var request = RedriveAllRequest("admin");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("late-task-handle", text, StringComparison.Ordinal);
        Assert.Equal(
            taskStarted ? 1 : 0,
            factory
                .Sqs.ReceivedCalls()
                .Count(call => call.GetMethodInfo().Name == nameof(IAmazonSQS.StartMessageMoveTaskAsync))
        );
        AssertPrivacy(factory, text);
    }

    [Fact]
    public async Task WhenTaskListingFails_ShouldReturnSafeFailureWithoutAwsDetails()
    {
        await using var factory = new InspectionApplicationFactory();
        factory
            .Sqs.GetQueueAttributesAsync(Arg.Any<GetQueueAttributesRequest>(), Arg.Any<CancellationToken>())
            .Returns(CountResponse());
        factory
            .Sqs.ListMessageMoveTasksAsync(Arg.Any<ListMessageMoveTasksRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ListMessageMoveTasksResponse>>(_ =>
                throw new InvalidOperationException($"{Identity} {PrivateContent} {Receipt}")
            );
        using var client = factory.CreateClient();
        using var request = StatusRequest("admin");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Command DLQ status failed.", text, StringComparison.Ordinal);
        AssertPrivacy(factory, text);
    }

    private static HttpRequestMessage RedriveAllRequest(string condition)
    {
        var request = Request(condition);
        request.RequestUri = new Uri("/admin/notification-commands/dlq/redrive-all", UriKind.Relative);

        return request;
    }

    [Theory]
    [InlineData("attributes")]
    [InlineData("tasks")]
    [InlineData("task-status")]
    public async Task WhenStatusCannotBeConfirmed_ShouldReturnSafeFailureWithoutReceivingContent(string condition)
    {
        await using var factory = new InspectionApplicationFactory();
        var attributes = CountResponse();
        if (condition == "attributes")
            attributes.HttpStatusCode = HttpStatusCode.BadGateway;
        factory
            .Sqs.GetQueueAttributesAsync(Arg.Any<GetQueueAttributesRequest>(), Arg.Any<CancellationToken>())
            .Returns(attributes);
        factory
            .Sqs.ListMessageMoveTasksAsync(Arg.Any<ListMessageMoveTasksRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ListMessageMoveTasksResponse
                {
                    HttpStatusCode = condition == "tasks" ? HttpStatusCode.BadGateway : HttpStatusCode.OK,
                    Results =
                    [
                        new ListMessageMoveTasksResultEntry
                        {
                            Status = condition == "task-status" ? PrivateContent : "RUNNING",
                            ApproximateNumberOfMessagesMoved = 0,
                            ApproximateNumberOfMessagesToMove = 10,
                        },
                    ],
                }
            );
        using var client = factory.CreateClient();
        using var request = StatusRequest("admin");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Command DLQ status failed.", body, StringComparison.Ordinal);
        Assert.Equal(condition == "attributes" ? 1 : 2, factory.Sqs.ReceivedCalls().Count());
        AssertPrivacy(factory, body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenQueueIdentityCannotBeConfirmed_ShouldNotStartWholeQueueRedrive(bool missingArn)
    {
        await using var factory = new InspectionApplicationFactory();
        factory
            .Sqs.GetQueueAttributesAsync(Arg.Any<GetQueueAttributesRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new GetQueueAttributesResponse
                {
                    HttpStatusCode = missingArn ? HttpStatusCode.OK : HttpStatusCode.BadGateway,
                    Attributes = missingArn ? [] : new() { ["QueueArn"] = DlqArn },
                }
            );
        using var client = factory.CreateClient();
        using var request = RedriveAllRequest("admin");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Command DLQ whole-queue redrive failed.", body, StringComparison.Ordinal);
        Assert.Single(factory.Sqs.ReceivedCalls());
        AssertPrivacy(factory, body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenCallerCancelsQueueOperation_ShouldCancelActualDependencyWithoutStartingTask(bool redriveAll)
    {
        await using var factory = new InspectionApplicationFactory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken dependencyToken = default;
        factory
            .Sqs.GetQueueAttributesAsync(Arg.Any<GetQueueAttributesRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                dependencyToken = call.ArgAt<CancellationToken>(1);
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, dependencyToken);
                return CountResponse();
            });
        using var client = factory.CreateClient();
        using var request = redriveAll ? RedriveAllRequest("admin") : StatusRequest("admin");
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = client.SendAsync(request, source.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.True(dependencyToken.IsCancellationRequested);
        Assert.Single(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
    }

    [Fact]
    public async Task WhenInspectionReplayContainsDuplicateIdentities_ShouldNotIssueSelectionsOrReadStorage()
    {
        await using var factory = new InspectionApplicationFactory();
        factory
            .Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.OK, Messages = [Message(), Message()] }
            );
        using var client = factory.CreateClient();
        using var request = Request("admin");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
        AssertPrivacy(factory, body);
    }

    [Theory]
    [InlineData("missing", HttpStatusCode.Unauthorized)]
    [InlineData("read", HttpStatusCode.Forbidden)]
    public async Task WhenSearchCallerIsNotAdmin_ShouldDenyBeforeReadingEvidence(
        string condition,
        HttpStatusCode status
    )
    {
        await using var factory = new InspectionApplicationFactory();
        using var client = factory.CreateClient();
        using var request = SearchRequest(condition);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(status, response.StatusCode);
        Assert.Empty(factory.SearchReader.ReceivedCalls());
        Assert.Empty(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
    }

    [Fact]
    public void WhenSearchQueryNamesUseDifferentCase_ShouldReadSupportedValues()
    {
        var values = new QueryCollection(
            Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery("?PAGESIZE=7&NOTIFICATIONKEY=exact-key&RESENDOFNOTIFICATIONKEY=parent-key")
        );

        var query = DeliveryRecordSearchQuery.Parse(values);

        Assert.Equal(7, query.PageSize);
        Assert.Equal("exact-key", query.NotificationKey);
        Assert.Equal("parent-key", query.ResendOfNotificationKey);
    }

    [Theory]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=999999999999999999999")]
    [InlineData("pageSize=1&pageSize=2")]
    [InlineData("notifyId=private-notify-id")]
    [InlineData("resendOf=private-parent-key")]
    [InlineData("resendOfNotificationKey=")]
    [InlineData("notificationKey=known-key&unexpected=value")]
    [InlineData("notificationKey=")]
    [InlineData("outcome=%00")]
    [InlineData("recordedFromUtc=2026-10-12T00:00:00")]
    [InlineData("recordedFromUtc=2026-10-12T00:00:00%2B01:00")]
    [InlineData("recordedFromUtc=2026-10-13T00:00:00Z&recordedToUtc=2026-10-12T00:00:00Z")]
    [InlineData("cursor=private-invalid-cursor")]
    public async Task WhenSearchQueryIsInvalid_ShouldRejectBeforeReadingEvidence(string query)
    {
        await using var factory = new InspectionApplicationFactory();
        using var client = factory.CreateClient();
        using var request = SearchRequest("admin", query);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Delivery record search query is invalid.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-invalid-cursor", body, StringComparison.Ordinal);
        Assert.Empty(factory.SearchReader.ReceivedCalls());
        Assert.Empty(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAdminSearchesRetainedEvidence_ShouldReturnSummaryThroughRealReaderWithoutEffects(bool validParent)
    {
        const string parent = "v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var mongo = Substitute.For<IMongoClient>();
        var database = Substitute.For<IMongoDatabase>();
        var collection = Substitute.For<IMongoCollection<BsonDocument>>();
        var cursor = Substitute.For<IAsyncCursor<BsonDocument>>();
        mongo.GetDatabase("inspection-test", Arg.Any<MongoDatabaseSettings>()).Returns(database);
        database
            .GetCollection<BsonDocument>("NotificationDeliveryRecord", Arg.Any<MongoCollectionSettings>())
            .Returns(collection);
        cursor.Current.Returns(
            new[]
            {
                new BsonDocument
                {
                    { "_id", ObjectId.Parse("000000000000000000000001") },
                    { "notificationKey", "v1:replacement" },
                    { "resendOfNotificationKey", validParent ? parent : "v1:parent/+?" },
                    { "notificationType", "submitted" },
                    { "outcome", "delivery-pending" },
                    { "recordedAtUtc", new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc) },
                    { "actionOccurredAtUtc", new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc) },
                },
            }
        );
        cursor.MoveNextAsync(Arg.Any<CancellationToken>()).Returns(true, false);
        collection
            .FindAsync(
                Arg.Any<FilterDefinition<BsonDocument>>(),
                Arg.Any<FindOptions<BsonDocument, BsonDocument>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(cursor);
        await using var factory = new InspectionApplicationFactory(searchMongo: mongo);
        using var client = factory.CreateClient();
        using var request = SearchRequest("admin", "notificationKey=v1%3Areplacement");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(text);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = Assert.Single(body.RootElement.GetProperty("records").EnumerateArray());
        Assert.Equal("v1:replacement", record.GetProperty("notificationKey").GetString());
        Assert.Equal("delivery-pending", record.GetProperty("outcome").GetString());
        Assert.Equal("2026-10-12T09:00:00Z", record.GetProperty("recordedAtUtc").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("notifyNotificationId").ValueKind);
        if (validParent)
        {
            Assert.Equal(parent, record.GetProperty("resendOfNotificationKey").GetString());
            Assert.Equal("/admin/notification-commands/delivery-records?notificationKey=v1%3A" + parent[3..], record.GetProperty("links").GetProperty("sourceRecord").GetString());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, record.GetProperty("resendOfNotificationKey").ValueKind);
            Assert.Equal(JsonValueKind.Null, record.GetProperty("links").ValueKind);
        }
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("nextCursor").ValueKind);
        string[] privateFields = ["recipient", "immutableFields", "attemptOwner", "emailAddress", "personalisation"];
        foreach (var field in privateFields)
            Assert.False(record.TryGetProperty(field, out _));
        Assert.Equal("FindAsync", Assert.Single(collection.ReceivedCalls()).GetMethodInfo().Name);
        Assert.Empty(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
        AssertPrivacy(factory, text);
    }

    [Fact]
    public async Task WhenSearchDependencyFails_ShouldReturnFixedFailureWithoutDependencyContent()
    {
        await using var factory = new InspectionApplicationFactory(productionLogging: true);
        factory
            .SearchReader.Search(Arg.Any<DeliveryRecordSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns<Task<DeliveryRecordSearchResult>>(_ => throw new InvalidOperationException(PrivateContent));
        using var client = factory.CreateClient();
        var query = "notificationKey=" + Uri.EscapeDataString(Identity);
        using var request = SearchRequest("admin", query);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var capture = factory.ProductionLogs!;
        await WaitForRequestLogs(capture, "/admin/notification-commands/delivery-records");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Delivery record search failed.", body, StringComparison.Ordinal);
        var failure = Assert.Single(
            capture.Events,
            logEvent => logEvent.MessageTemplate.Text == "Delivery record search failed."
        );
        Assert.Equal(LogEventLevel.Error, failure.Level);
        Assert.Null(failure.Exception);
        foreach (var sensitive in new[] { Identity, Uri.EscapeDataString(Identity), PrivateContent })
        {
            Assert.All(
                capture.Events,
                logEvent => Assert.DoesNotContain(sensitive, StructuredLogText(logEvent), StringComparison.Ordinal)
            );
            Assert.All(capture.EcsOutput, output => Assert.DoesNotContain(sensitive, output, StringComparison.Ordinal));
        }
        AssertPrivacy(factory, body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenSearchIsCancelled_ShouldBoundMongoWorkAndDistinguishCallerCancellation(bool callerCancels)
    {
        var mongo = Substitute.For<IMongoClient>();
        var database = Substitute.For<IMongoDatabase>();
        var collection = Substitute.For<IMongoCollection<BsonDocument>>();
        mongo.GetDatabase("inspection-test", Arg.Any<MongoDatabaseSettings>()).Returns(database);
        database
            .GetCollection<BsonDocument>("NotificationDeliveryRecord", Arg.Any<MongoCollectionSettings>())
            .Returns(collection);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken dependencyToken = default;
        collection
            .FindAsync(
                Arg.Any<FilterDefinition<BsonDocument>>(),
                Arg.Any<FindOptions<BsonDocument, BsonDocument>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(async call =>
            {
                dependencyToken = call.ArgAt<CancellationToken>(2);
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, dependencyToken);

                return Substitute.For<IAsyncCursor<BsonDocument>>();
            });
        await using var factory = new InspectionApplicationFactory(
            new() { ["CommandDlqAdministration:DependencyTimeoutSeconds"] = "1" },
            searchMongo: mongo
        );
        using var client = factory.CreateClient();
        using var request = SearchRequest("admin");
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = client.SendAsync(request, source.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        if (callerCancels)
        {
            await source.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        }
        else
        {
            using var response = await pending;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            AssertPrivacy(factory, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        Assert.True(dependencyToken.IsCancellationRequested);
        Assert.Equal(
            callerCancels ? 0 : 1,
            factory.Logs.Messages.Count(message =>
                message.Contains("Delivery record search failed.", StringComparison.Ordinal)
            )
        );
        Assert.Empty(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
    }

    [Theory]
    [InlineData("admin", false, 200)]
    [InlineData("admin", true, 200)]
    [InlineData("missing", true, 401)]
    [InlineData("invalid", true, 400)]
    public async Task WhenAdminSearchContainsEvidence_ShouldKeepItOutOfProductionRequestLogs(
        string caller,
        bool encoded,
        int status
    )
    {
        const string key = "v1:0000000000000000000000000000000000000000000000000000000000000001";
        const string notifyId = "12345678-1234-1234-1234-123456789abc";
        const string encodedKey = "v1%3A0000000000000000000000000000000000000000000000000000000000000001";
        const string encodedNotifyId = "12345678%2D1234%2D1234%2D1234%2D123456789abc";
        const string correlation = "request-log-privacy-correlation";
        var path = encoded
            ? "/ADMIN/notification-commands/delivery-records"
            : "/admin/notification-commands/delivery-records";
        var query =
            "notificationKey="
            + (encoded ? encodedKey : key)
            + "&notifyNotificationId="
            + (encoded ? encodedNotifyId : notifyId)
            + (caller == "invalid" ? "&pageSize=0" : "");
        await using var factory = new InspectionApplicationFactory(productionLogging: true);
        factory
            .SearchReader.Search(Arg.Any<DeliveryRecordSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new DeliveryRecordSearchResult([], null));
        using var client = factory.CreateClient();
        using var request = SearchRequest(caller == "invalid" ? "admin" : caller, query);
        request.RequestUri = new Uri(path + "?" + query, UriKind.Relative);
        request.Headers.Add("x-cdp-request-id", correlation);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var capture = factory.ProductionLogs!;
        await WaitForRequestLogs(capture, path);

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        AssertRequestLogMetadata(capture, path, status);
        Assert.Contains(capture.EcsOutput, output => output.Contains(correlation, StringComparison.Ordinal));
        Assert.Contains(
            capture.Events,
            logEvent => StructuredLogText(logEvent).Contains(correlation, StringComparison.Ordinal)
        );
        if (caller == "admin")
            await factory
                .SearchReader.Received(1)
                .Search(
                    Arg.Is<DeliveryRecordSearchQuery>(search =>
                        search.NotificationKey == key && search.NotifyNotificationId == notifyId
                    ),
                    Arg.Any<CancellationToken>()
                );
        else
            Assert.Empty(factory.SearchReader.ReceivedCalls());
        var contexts = capture
            .Events.Select(logEvent =>
                logEvent.Properties.TryGetValue("HttpContext", out var value)
                && value is ScalarValue { Value: SpecialProperties.HttpContextEnrichments context }
                    ? context
                    : null
            )
            .Where(context => context?.Url?.Path == path)
            .ToArray();
        Assert.NotEmpty(contexts);
        foreach (var sensitive in new[] { key, notifyId, encodedKey, encodedNotifyId })
        {
            Assert.All(
                capture.Events,
                logEvent =>
                {
                    Assert.DoesNotContain(sensitive, StructuredLogText(logEvent), StringComparison.Ordinal);
                    Assert.DoesNotContain(sensitive, logEvent.RenderMessage(), StringComparison.Ordinal);
                }
            );
            Assert.All(capture.EcsOutput, output => Assert.DoesNotContain(sensitive, output, StringComparison.Ordinal));
        }
        Assert.All(
            contexts,
            context =>
            {
                var url = Assert.IsType<Elastic.CommonSchema.Url>(context!.Url);
                Assert.Equal("", url.Query);
                Assert.DoesNotContain("?", url.Full, StringComparison.Ordinal);
                Assert.DoesNotContain("?", url.Original, StringComparison.Ordinal);
            }
        );
        Assert.Empty(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
    }

    [Theory]
    [InlineData("/admin", true)]
    [InlineData("/administrator", false)]
    public async Task WhenPathHasAdminPrefix_ShouldRedactOnlyTheAdminSegment(string path, bool redact)
    {
        const string marker = "ordinary-visible-query-marker";
        await using var factory = new InspectionApplicationFactory(productionLogging: true);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path + "?control=" + marker, TestContext.Current.CancellationToken);
        var capture = factory.ProductionLogs!;
        await WaitForRequestLogs(capture, path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertRequestLogMetadata(capture, path, 404);
        if (redact)
        {
            Assert.All(
                capture.Events,
                logEvent => Assert.DoesNotContain(marker, StructuredLogText(logEvent), StringComparison.Ordinal)
            );
            Assert.All(capture.EcsOutput, output => Assert.DoesNotContain(marker, output, StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(
                capture.Events,
                logEvent => StructuredLogText(logEvent).Contains(marker, StringComparison.Ordinal)
            );
            Assert.Contains(capture.EcsOutput, output => output.Contains(marker, StringComparison.Ordinal));
        }
        Assert.Empty(factory.SearchReader.ReceivedCalls());
        Assert.Empty(factory.Sqs.ReceivedCalls());
        Assert.Empty(factory.Store.ReceivedCalls());
        Assert.Empty(factory.Notify.ReceivedCalls());
    }

    private static async Task WaitForRequestLogs(AdminRequestLogCapture capture, string path)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (
                !capture.Events.Any(logEvent =>
                    logEvent.MessageTemplate.Text.StartsWith("Request finished", StringComparison.Ordinal)
                    && RequestLogPath(logEvent) == path
                )
            )
                await Task.Delay(TimeSpan.FromMilliseconds(10), source.Token);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            Assert.Fail("Request-finish capture was not observed. " + DescribeLogProperties(capture));
        }
    }

    private static void AssertRequestLogMetadata(AdminRequestLogCapture capture, string path, int status)
    {
        Assert.False(capture.Overflowed);
        var events = capture.Events.Where(logEvent => RequestLogPath(logEvent) == path).ToArray();
        Assert.Contains(
            events,
            logEvent => logEvent.MessageTemplate.Text.StartsWith("Request starting", StringComparison.Ordinal)
        );
        var finished = Assert.Single(
            events,
            logEvent => logEvent.MessageTemplate.Text.StartsWith("Request finished", StringComparison.Ordinal)
        );
        Assert.Equal(status, Assert.IsType<int>(Assert.IsType<ScalarValue>(finished.Properties["StatusCode"]).Value));
        Assert.Equal("GET", Assert.IsType<string>(Assert.IsType<ScalarValue>(finished.Properties["Method"]).Value));
        Assert.Contains(capture.EcsOutput, output => output.Contains(path, StringComparison.Ordinal));
    }

    private static string? RequestLogPath(LogEvent logEvent) =>
        ReadLogPath(logEvent, "RequestPath")
        ?? ReadLogPath(logEvent, "Path")
        ?? (
            logEvent.Properties.TryGetValue("HttpContext", out var value)
            && value is ScalarValue { Value: SpecialProperties.HttpContextEnrichments context }
                ? context.Url?.Path
                : null
        );

    private static string? ReadLogPath(LogEvent logEvent, string property)
    {
        if (!logEvent.Properties.TryGetValue(property, out var value) || value is not ScalarValue scalar)
            return null;

        return scalar.Value switch
        {
            string text => text,
            PathString path => path.Value,
            _ => null,
        };
    }

    private static string DescribeLogProperties(AdminRequestLogCapture capture)
    {
        var requestProperties = capture
            .Events.Where(logEvent => logEvent.MessageTemplate.Text.StartsWith("Request ", StringComparison.Ordinal))
            .Take(4)
            .Select(logEvent =>
                string.Join(
                    ", ",
                    logEvent
                        .Properties.OrderBy(property => property.Key)
                        .Take(16)
                        .Select(property => property.Key + ":" + LogPropertyType(property.Value))
                )
            );

        return "Captured events: "
            + capture.Events.Count
            + "; request property names/types: "
            + string.Join("; ", requestProperties);
    }

    private static string LogPropertyType(LogEventPropertyValue value) =>
        value is ScalarValue scalar ? scalar.Value?.GetType().Name ?? "null" : value.GetType().Name;

    private static string StructuredLogText(LogEvent logEvent) =>
        JsonSerializer.Serialize(
            logEvent.Properties.ToDictionary(
                property => property.Key,
                property => property.Value is ScalarValue scalar ? scalar.Value : property.Value.ToString()
            )
        );

    private static HttpRequestMessage SearchRequest(string condition, string? query = null)
    {
        var request = Request(condition);
        request.Method = HttpMethod.Get;
        request.RequestUri = new Uri(
            "/admin/notification-commands/delivery-records" + (query is null ? "" : "?" + query),
            UriKind.Relative
        );

        return request;
    }

    private static void ConfigureQueueIdentities(InspectionApplicationFactory factory, bool sameQueue = false)
    {
        factory
            .Sqs.GetQueueAttributesAsync(Arg.Any<GetQueueAttributesRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new GetQueueAttributesResponse
            {
                HttpStatusCode = HttpStatusCode.OK,
                Attributes = new()
                {
                    ["QueueArn"] =
                        sameQueue
                        || call.Arg<GetQueueAttributesRequest>()
                            .QueueUrl.EndsWith("commands-dlq.fifo", StringComparison.Ordinal)
                            ? DlqArn
                            : CommandArn,
                },
            });
    }

    private static GetQueueAttributesResponse CountResponse() =>
        new()
        {
            HttpStatusCode = HttpStatusCode.OK,
            Attributes = new()
            {
                ["QueueArn"] = DlqArn,
                ["ApproximateNumberOfMessages"] = "10",
                ["ApproximateNumberOfMessagesNotVisible"] = "4",
                ["ApproximateNumberOfMessagesDelayed"] = "2",
            },
        };

    private static HttpRequestMessage StatusRequest(string condition)
    {
        var request = Request(condition);
        request.Method = HttpMethod.Get;
        request.RequestUri = new Uri("/admin/notification-commands/dlq/status", UriKind.Relative);

        return request;
    }

    private static void AssertPrivacy(InspectionApplicationFactory factory, string text)
    {
        foreach (
            var value in new[]
            {
                Identity,
                PrivateContent,
                Receipt,
                Secret,
                "private-evidence-secret",
                "private-lane-secret",
                "wrong-private-secret",
            }
        )
        {
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
            Assert.All(
                factory.Logs.Messages,
                message => Assert.DoesNotContain(value, message, StringComparison.Ordinal)
            );
        }
    }

    private static string DecodePayload(string token)
    {
        var value = token.Split('.')[1].Replace('-', '+').Replace('_', '/');

        return Encoding.UTF8.GetString(Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '=')));
    }

    private static HttpRequestMessage Request(string condition)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route);
        var header = condition switch
        {
            "missing" => null,
            "malformed" => "Basic: private-header",
            "multiple" => $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{Secret}"))}",
            "empty-parameter" => "Basic",
            "base64" => "Basic private-invalid-base64",
            "utf8" => "Basic /w==",
            "empty-client" => $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($":{Secret}"))}",
            "empty-secret" => $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:"))}",
            "missing-colon" => $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("admin"))}",
            "wrong" => $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:wrong-private-secret"))}",
            "bearer" => $"Bearer {Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{Secret}"))}",
            _ => $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{condition}:{Secret}"))}",
        };
        if (header is not null)
            request.Headers.TryAddWithoutValidation("Authorization", header);
        if (condition == "multiple")
            request.Headers.TryAddWithoutValidation("Authorization", header);

        return request;
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
                s_jsonOptions
            ),
            Attributes = new() { ["ApproximateReceiveCount"] = "3", ["SentTimestamp"] = "1790812800000" },
        };

    private sealed class InspectionApplicationFactory(
        Dictionary<string, string?>? overrides = null,
        bool emptyAcl = false,
        IMongoClient? searchMongo = null,
        bool productionLogging = false
    ) : WebApplicationFactory<Program>
    {
        public IAmazonSQS Sqs { get; } = CreateSqs();
        public INotificationDeliveryRecordStore Store { get; } = CreateStore();
        public IDeliveryRecordSearchReader SearchReader { get; } = Substitute.For<IDeliveryRecordSearchReader>();
        public INotifyEmailClient Notify { get; } = Substitute.For<INotifyEmailClient>();
        public RecordingLogs Logs { get; } = new();
        public AdminRequestLogCapture? ProductionLogs { get; } = productionLogging ? new() : null;

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await base.DisposeAsync();
            }
            finally
            {
                ProductionLogs?.Dispose();
            }
        }

        private static IAmazonSQS CreateSqs()
        {
            var sqs = Substitute.For<IAmazonSQS>();
            sqs.ListMessageMoveTasksAsync(Arg.Any<ListMessageMoveTasksRequest>(), Arg.Any<CancellationToken>())
                .Returns(new ListMessageMoveTasksResponse { HttpStatusCode = HttpStatusCode.OK, Results = [] });
            sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(new ReceiveMessageResponse { HttpStatusCode = HttpStatusCode.OK, Messages = [Message()] });

            return sqs;
        }

        private static INotificationDeliveryRecordStore CreateStore()
        {
            var store = Substitute.For<INotificationDeliveryRecordStore>();
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
                if (productionLogging)
                    services.AddSingleton<ILoggerFactory>(
                        serviceProvider => new Serilog.Extensions.Logging.SerilogLoggerFactory(
                            serviceProvider.GetRequiredService<Serilog.ILogger>(),
                            dispose: false
                        )
                    );
                else
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
                if (searchMongo is null)
                {
                    services.RemoveAll<IDeliveryRecordSearchReader>();
                    services.AddSingleton(SearchReader);
                }
                else
                {
                    services.RemoveAll<IMongoClient>();
                    services.AddSingleton(searchMongo);
                }
                var completion = new MongoMigrationCompletion();
                completion.MarkCompleted();
                services.AddSingleton(completion);
                var startup = new ApplicationStartup();
                startup.MarkStarted();
                services.AddSingleton(startup);
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var values = new Dictionary<string, string?>
            {
                ["AWS_EMF_ENABLED"] = "false",
                ["AnalyticsEventConsumer:ProcessingEnabled"] = "false",
                ["NotificationCommandDelivery:QueueUrl"] = "http://sqs.local/commands.fifo",
                ["NotificationCommandDelivery:EvidenceDigestSecret"] = "private-evidence-secret",
                ["NotificationCommandDelivery:RecipientLaneSecret"] = "private-lane-secret",
                ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
                ["Mongo:DatabaseName"] = "inspection-test",
                ["Notify:ApiKey"] = NotifyTestCredentials.ApiKey,
                ["Notify:BaseAddress"] = "http://notify.local",
                ["CommandDlqAdministration:QueueUrl"] = "http://sqs.local/commands-dlq.fifo",
                ["Acl:Clients:admin:Type"] = "ApiKey",
                ["Acl:Clients:admin:Secret"] = Secret,
                ["Acl:Clients:admin:Scopes:0"] = "admin",
                ["Acl:Clients:oauth:Type"] = "OAuth",
                ["Acl:Clients:oauth:Secret"] = Secret,
                ["Acl:Clients:oauth:Scopes:0"] = "read",
                ["Acl:Clients:read:Type"] = "ApiKey",
                ["Acl:Clients:read:Secret"] = Secret,
                ["Acl:Clients:read:Scopes:0"] = "read",
                ["Acl:Clients:write:Type"] = "ApiKey",
                ["Acl:Clients:write:Secret"] = Secret,
                ["Acl:Clients:write:Scopes:0"] = "write",
            };
            if (ProductionLogs is { } capture)
            {
                var captureValues = new Dictionary<string, string?>
                {
                    ["Serilog:Using:0"] = typeof(AdminRequestLogCaptureSinkExtensions).Assembly.GetName().Name,
                    ["Serilog:WriteTo:1:Name"] = "CaptureAdminRequests",
                    ["Serilog:WriteTo:1:Args:captureId"] = capture.Id,
                };
                builder.ConfigureAppConfiguration(
                    (_, configuration) => configuration.AddInMemoryCollection(captureValues)
                );
            }
            if (emptyAcl)
                foreach (
                    var key in values
                        .Keys.Where(key => key.StartsWith("Acl:Clients:", StringComparison.Ordinal))
                        .ToArray()
                )
                    values.Remove(key);
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
