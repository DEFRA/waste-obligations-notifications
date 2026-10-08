using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Administration;

public sealed class CommandDlqRedriver(
    IAmazonSQS sqs,
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
            EnsureTimely(started, budget, selection, source.Token);
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
            EnsureTimely(started, budget, selection, source.Token);
            var selected = ReadSelection(response, selection);
            if (selected is null)
                return CommandDlqRedriveResult.SelectionUnavailable;
            var (message, command) = selected.Value;
            await PublishAndDelete(message, command, started, budget, selection, source.Token);

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

    public async Task<IReadOnlyList<CommandDlqMessageRedriveResult>?> RedriveBatch(
        IReadOnlyList<string?> selectionTokens,
        CancellationToken cancellationToken
    )
    {
        var started = Stopwatch.GetTimestamp();
        if (selectionTokens.Count is < 1 or > 10)
            return null;
        var batch = selectionTokens.Select(selections.Validate).ToArray();
        if (batch.Any(selection => selection is null))
            return null;
        var first = batch[0]!;
        if (
            batch.Select(selection => selection!.MessageId).Distinct().Count() != batch.Length
            || batch.Any(selection =>
                selection!.ReceiveRequestAttemptId != first.ReceiveRequestAttemptId
                || selection.ExpiresAtUtc != first.ExpiresAtUtc
                || selection.VisibilityTimeoutSeconds != first.VisibilityTimeoutSeconds
                || selection.MaxNumberOfMessages != first.MaxNumberOfMessages
            )
        )
            return null;
        var remaining = selections.RemainingLifetime(first);
        var dependencyBudget = TimeSpan.FromSeconds(administration.Value.DependencyTimeoutSeconds);
        var budget = remaining < dependencyBudget ? remaining : dependencyBudget;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            EnsureTimely(started, budget, first, source.Token);
            source.CancelAfter(budget - Stopwatch.GetElapsedTime(started));
            var response = await sqs.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = administration.Value.QueueUrl,
                    ReceiveRequestAttemptId = first.ReceiveRequestAttemptId,
                    MaxNumberOfMessages = first.MaxNumberOfMessages ?? 1,
                    WaitTimeSeconds = 0,
                    VisibilityTimeout = first.VisibilityTimeoutSeconds,
                    MessageAttributeNames = ["All"],
                    MessageSystemAttributeNames = ["ApproximateReceiveCount", "SentTimestamp"],
                },
                source.Token
            );
            EnsureTimely(started, budget, first, source.Token);
            if (
                response.HttpStatusCode != HttpStatusCode.OK
                || response.Messages?.Count > (first.MaxNumberOfMessages ?? 1)
                || response.Messages?.Select(message => message.MessageId).Distinct().Count()
                    != response.Messages?.Count
            )
                throw new InvalidOperationException("Command DLQ replay did not succeed.");
            var results = new List<CommandDlqMessageRedriveResult>();
            foreach (var selection in batch.OfType<CommandDlqSelection>())
            {
                try
                {
                    var selected = ReadSelection(response, selection);
                    if (selected is null)
                    {
                        results.Add(new(selection.MessageId, "unavailable"));
                        continue;
                    }
                    var (message, command) = selected.Value;
                    await PublishAndDelete(message, command, started, budget, selection, source.Token);
                    results.Add(new(selection.MessageId, "redriven"));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    diagnostics.RedriveFailed();
                    results.Add(new(selection.MessageId, "failed"));
                }
            }

            return results;
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

    private async Task PublishAndDelete(
        Message message,
        NotificationCommand command,
        long started,
        TimeSpan budget,
        CommandDlqSelection selection,
        CancellationToken token
    )
    {
        EnsureTimely(started, budget, selection, token);
        var sent = await sqs.SendMessageAsync(
            new SendMessageRequest
            {
                QueueUrl = delivery.Value.QueueUrl,
                MessageBody = message.Body,
                MessageAttributes = message.MessageAttributes ?? [],
                MessageGroupId = digest.CreateRecipientLane(command.EmailAddress),
                MessageDeduplicationId = CreateTransportDeduplicationId(message.MessageId),
            },
            token
        );
        EnsureTimely(started, budget, selection, token);
        if (sent.HttpStatusCode != HttpStatusCode.OK || sent.MessageId is not { Length: > 0 and <= 100 })
            throw new InvalidOperationException("Redriven command publication was not confirmed.");
        var deleted = await sqs.DeleteMessageAsync(
            new DeleteMessageRequest
            {
                QueueUrl = administration.Value.QueueUrl,
                ReceiptHandle = message.ReceiptHandle,
            },
            token
        );
        EnsureTimely(started, budget, selection, token);
        if (deleted.HttpStatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException("Redriven command removal was not confirmed.");
        diagnostics.Redriven(command.NotificationType);
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
        if (
            response.HttpStatusCode != HttpStatusCode.OK
            || response.Messages?.Count > (selection.MaxNumberOfMessages ?? 1)
        )
            throw new InvalidOperationException("Command DLQ replay did not succeed.");
        var message = response.Messages?.SingleOrDefault(message => message.MessageId == selection.MessageId);
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
