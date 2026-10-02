using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Administration;

public sealed class CommandDlqRedriver(
    IAmazonSQS sqs,
    MongoMigrationReadiness readiness,
    INotificationCommandDigest digest,
    CommandDlqSelectionTokens selections,
    IOptions<CommandDlqAdministrationOptions> administration,
    IOptions<NotificationCommandDeliveryOptions> delivery,
    CommandDlqDiagnostics diagnostics
)
{
    public async Task<CommandDlqRedriveResult> Redrive(string? selectionToken, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var selection = selections.Validate(selectionToken);
        if (selection is null)
            return CommandDlqRedriveResult.InvalidSelection;
        var remaining = selections.RemainingLifetime(selection);
        var dependencyBudget = TimeSpan.FromSeconds(administration.Value.DependencyTimeoutSeconds);
        var budget = remaining < dependencyBudget ? remaining : dependencyBudget;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            EnsureTimely(started, budget, selection, source.Token);
            source.CancelAfter(budget - Stopwatch.GetElapsedTime(started));
            await readiness.Wait(source.Token);
            EnsureTimely(started, budget, selection, source.Token);
            var response = await sqs.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = administration.Value.QueueUrl,
                    ReceiveRequestAttemptId = selection.ReceiveRequestAttemptId,
                    MaxNumberOfMessages = 1,
                    WaitTimeSeconds = 0,
                    VisibilityTimeout = selection.VisibilityTimeoutSeconds,
                    MessageAttributeNames = ["All"],
                    MessageSystemAttributeNames = ["ApproximateReceiveCount", "SentTimestamp"],
                },
                source.Token
            );
            EnsureTimely(started, budget, selection, source.Token);
            var selected = ReadSelection(response, selection);
            if (selected is null)
                return CommandDlqRedriveResult.SelectionUnavailable;
            var (message, command) = selected.Value;
            EnsureTimely(started, budget, selection, source.Token);
            var sent = await sqs.SendMessageAsync(
                new SendMessageRequest
                {
                    QueueUrl = delivery.Value.QueueUrl,
                    MessageBody = message.Body,
                    MessageAttributes = message.MessageAttributes ?? [],
                    MessageGroupId = digest.CreateRecipientLane(command.EmailAddress),
                    MessageDeduplicationId = CreateTransportDeduplicationId(message.MessageId),
                },
                source.Token
            );
            EnsureTimely(started, budget, selection, source.Token);
            if (sent.HttpStatusCode != HttpStatusCode.OK || sent.MessageId is not { Length: > 0 and <= 100 })
                throw new InvalidOperationException("Redriven command publication was not confirmed.");
            var deleted = await sqs.DeleteMessageAsync(
                new DeleteMessageRequest
                {
                    QueueUrl = administration.Value.QueueUrl,
                    ReceiptHandle = message.ReceiptHandle,
                },
                source.Token
            );
            EnsureTimely(started, budget, selection, source.Token);
            if (deleted.HttpStatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("Redriven command removal was not confirmed.");
            diagnostics.Redriven(command.NotificationType);

            return CommandDlqRedriveResult.Redriven;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw RedriveFailure();
        }
    }

    private void EnsureTimely(long started, TimeSpan budget, CommandDlqSelection selection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(started) >= budget || selections.RemainingLifetime(selection) <= TimeSpan.Zero)
            throw new TimeoutException("Command DLQ redrive exceeded its deadline.");
    }

    private string CreateTransportDeduplicationId(string messageId)
    {
        var sourceIdentity = JsonSerializer.Serialize(new[] { administration.Value.QueueUrl, messageId });
        var value = Encoding.UTF8.GetBytes($"v1:command-dlq-redrive:{sourceIdentity}");
        var key = Encoding.UTF8.GetBytes(delivery.Value.EvidenceDigestSecret);

        return $"v1:{Convert.ToHexStringLower(HMACSHA256.HashData(key, value))}";
    }

    private InvalidOperationException RedriveFailure()
    {
        diagnostics.RedriveFailed();

        return new InvalidOperationException("Command DLQ redrive failed.");
    }

    private (Message Message, NotificationCommand Command)? ReadSelection(
        ReceiveMessageResponse response,
        CommandDlqSelection selection
    )
    {
        if (response.HttpStatusCode != HttpStatusCode.OK || response.Messages is { Count: > 1 })
            throw new InvalidOperationException("Command DLQ replay did not succeed.");
        var message = response.Messages?.SingleOrDefault();
        if (message is null || message.MessageId != selection.MessageId || string.IsNullOrEmpty(message.ReceiptHandle))
            return null;
        NotificationCommand command;
        try
        {
            command = NotificationCommandMessageReader.Read(message).NormaliseRecipient();
        }
        catch (Exception)
        {
            return null;
        }

        return digest.CreateImmutableFieldsDigest(command) == selection.ImmutableFieldsDigest
            ? (message, command)
            : null;
    }
}
