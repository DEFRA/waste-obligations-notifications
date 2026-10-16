using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Administration;

public sealed class CommandDlqResender(
    IAmazonSQS sqs,
    INotificationDeliveryRecordStore store,
    INotificationCommandDigest digest,
    CommandDlqSelectionTokens selections,
    IOptions<CommandDlqAdministrationOptions> administration,
    IOptions<NotificationCommandDeliveryOptions> delivery,
    CommandDlqDiagnostics diagnostics
)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CommandDlqResendResult?> Resend(
        CommandDlqSelection selection,
        CancellationToken cancellationToken
    )
    {
        var started = Stopwatch.GetTimestamp();
        var remaining = selections.RemainingLifetime(selection);
        var dependencyBudget = TimeSpan.FromSeconds(administration.Value.DependencyTimeoutSeconds);
        var budget = remaining < dependencyBudget ? remaining : dependencyBudget;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            EnsureTimely();
            source.CancelAfter(budget - Stopwatch.GetElapsedTime(started));

            var response = await sqs.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = administration.Value.QueueUrl,
                    ReceiveRequestAttemptId = selection.ReceiveRequestAttemptId,
                    MaxNumberOfMessages = selection.MaxNumberOfMessages ?? 1,
                    WaitTimeSeconds = 0,
                    VisibilityTimeout = selection.VisibilityTimeoutSeconds,
                    MessageAttributeNames = ["All"],
                    MessageSystemAttributeNames = ["ApproximateReceiveCount", "SentTimestamp"],
                },
                source.Token
            );
            EnsureTimely();
            var selected = ReadSelection(response, selection);
            if (selected is null)
                return null;
            var (message, command, sourceNotificationKey) = selected.Value;

            EnsureTimely();
            var state = await store.Inspect(command, source.Token);
            EnsureTimely();
            if (state.Classification != NotificationDeliveryClassifications.DeliveryAccepted)
                return null;

            // One immediate source always has one replacement, independent of selections and transport copies.
            var replacement = command with
            {
                IdempotencyKey = "resend:" + sourceNotificationKey,
                ResendOfNotificationKey = sourceNotificationKey,
            };
            replacement.Validate();

            var publication = new SendMessageRequest
            {
                QueueUrl = delivery.Value.QueueUrl,
                MessageBody = JsonSerializer.Serialize(replacement, s_jsonOptions),
                MessageGroupId = digest.CreateRecipientLane(replacement.EmailAddress),
                MessageDeduplicationId = replacement.IdempotencyKey,
            };
            EnsureTimely();
            var published = await sqs.SendMessageAsync(publication, source.Token);
            EnsureTimely();
            if (published.HttpStatusCode != HttpStatusCode.OK || published.MessageId is not { Length: > 0 and <= 100 })
                throw new InvalidOperationException("Replacement publication was not confirmed.");

            var deleted = await sqs.DeleteMessageAsync(
                new DeleteMessageRequest
                {
                    QueueUrl = administration.Value.QueueUrl,
                    ReceiptHandle = message.ReceiptHandle,
                },
                source.Token
            );
            EnsureTimely();
            if (deleted.HttpStatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("Selected source removal was not confirmed.");
            diagnostics.Resent(command.NotificationType);

            return new(sourceNotificationKey, digest.CreateIdempotencyKeyDigest(replacement.IdempotencyKey));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            diagnostics.ResendFailed();
            throw new InvalidOperationException("Command DLQ resend failed.");
        }

        void EnsureTimely()
        {
            source.Token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= budget || selections.RemainingLifetime(selection) <= TimeSpan.Zero)
                throw new TimeoutException("Command DLQ resend exceeded its deadline.");
        }
    }

    private (Message Message, NotificationCommand Command, string NotificationKey)? ReadSelection(
        ReceiveMessageResponse response,
        CommandDlqSelection selection
    )
    {
        if (
            response.HttpStatusCode != HttpStatusCode.OK
            || response.Messages?.Count > (selection.MaxNumberOfMessages ?? 1)
        )
            throw new InvalidOperationException("Command DLQ replay was not confirmed.");
        var message = response.Messages?.SingleOrDefault(item => item.MessageId == selection.MessageId);
        if (message is null || string.IsNullOrEmpty(message.ReceiptHandle))
            return null;

        var command = ReadCommand(message);
        if (
            command is null
            || selection.NotificationKey is null
            || !selection.Matches(command, digest)
        )
            return null;

        return (message, command, selection.NotificationKey);
    }

    private static NotificationCommand? ReadCommand(Message message)
    {
        try
        {
            return NotificationCommandMessageReader.Read(message).NormaliseRecipient();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
