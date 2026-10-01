using System.Diagnostics;
using System.Net;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Administration;

public sealed class CommandDlqDiscarder(
    IAmazonSQS sqs,
    INotificationDeliveryRecordStore store,
    MongoMigrationReadiness readiness,
    INotificationCommandDigest digest,
    CommandDlqSelectionTokens selections,
    IOptions<CommandDlqAdministrationOptions> administration,
    CommandDlqDiagnostics diagnostics
)
{
    public async Task<CommandDlqDiscardResult> Discard(string? selectionToken, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var selection = selections.Validate(selectionToken);
        if (selection is null)
            return CommandDlqDiscardResult.InvalidSelection;
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
                    VisibilityTimeout = Math.Clamp(
                        (int)Math.Ceiling(selections.RemainingLifetime(selection).TotalSeconds),
                        1,
                        43200
                    ),
                    MessageAttributeNames = ["All"],
                    MessageSystemAttributeNames = ["ApproximateReceiveCount", "SentTimestamp"],
                },
                source.Token
            );
            EnsureTimely(started, budget, selection, source.Token);
            var selected = ReadSelection(response, selection);
            if (selected is null)
                return CommandDlqDiscardResult.Conflict;
            var (message, command) = selected.Value;
            EnsureTimely(started, budget, selection, source.Token);
            var abandoned = await store.RecordAbandonment(command, source.Token);
            EnsureTimely(started, budget, selection, source.Token);
            if (abandoned is not (AbandonmentResult.Recorded or AbandonmentResult.AlreadyAbandoned))
                return CommandDlqDiscardResult.Conflict;
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
                throw new InvalidOperationException("Discarded command removal was not confirmed.");
            diagnostics.Discarded(command.NotificationType);

            return CommandDlqDiscardResult.Discarded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw DiscardFailure();
        }
    }

    private void EnsureTimely(long started, TimeSpan budget, CommandDlqSelection selection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(started) >= budget || selections.RemainingLifetime(selection) <= TimeSpan.Zero)
            throw new TimeoutException("Command DLQ discard exceeded its deadline.");
    }

    private InvalidOperationException DiscardFailure()
    {
        diagnostics.DiscardFailed();

        return new InvalidOperationException("Command DLQ discard failed.");
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
