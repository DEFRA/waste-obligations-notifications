using System.Diagnostics;
using System.Globalization;
using System.Net;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Administration;

public sealed class CommandDlqInspector(
    IAmazonSQS sqs,
    INotificationDeliveryRecordStoreFactory storeFactory,
    MongoMigrationReadiness readiness,
    INotificationCommandDigest digest,
    CommandDlqSelectionTokens selections,
    IOptions<CommandDlqAdministrationOptions> administration,
    CommandDlqDiagnostics diagnostics
)
{
    private const string UnavailableHistory = "Historical dependency error details are unavailable.";

    public async Task<CommandDlqInspection?> Inspect(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(administration.Value.DependencyTimeoutSeconds));
        try
        {
            await readiness.Wait(source.Token);
            EnsureTimely(started, source.Token);
            // Both the replay window and selection expiry begin before SQS can make this message invisible.
            var selectionStarted = Stopwatch.GetTimestamp();
            var expiresAtUtc = selections.CreateExpiry();
            var attemptId = Guid.NewGuid().ToString();
            var response = await sqs.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = administration.Value.QueueUrl,
                    MaxNumberOfMessages = 1,
                    WaitTimeSeconds = 0,
                    VisibilityTimeout = administration.Value.SelectionLifetimeSeconds,
                    ReceiveRequestAttemptId = attemptId,
                    MessageAttributeNames = ["All"],
                    MessageSystemAttributeNames = ["ApproximateReceiveCount", "SentTimestamp"],
                },
                source.Token
            );
            EnsureTimely(started, source.Token);
            if (response.HttpStatusCode != HttpStatusCode.OK || response.Messages is { Count: > 1 })
                throw new InvalidOperationException("Command DLQ receive did not succeed.");
            var message = response.Messages?.SingleOrDefault();
            if (message is null)
                return null;
            if (message.MessageId is not { Length: > 0 and <= 100 })
                throw new InvalidOperationException("Command DLQ message identity is invalid.");
            var command = ReadCommand(message);
            EnsureTimely(started, source.Token);
            if (command is null)
            {
                diagnostics.InvalidCommand();

                return new(
                    null,
                    null,
                    null,
                    SentAt(message),
                    ReceiveCount(message),
                    "invalid-or-unsupported-command",
                    UnavailableHistory,
                    null,
                    null,
                    null,
                    null
                );
            }
            var state = await storeFactory.GetRecordStore().Inspect(command, source.Token);
            EnsureTimely(started, source.Token);
            if (
                Stopwatch.GetElapsedTime(selectionStarted)
                >= TimeSpan.FromSeconds(administration.Value.SelectionLifetimeSeconds)
            )
                throw new TimeoutException("Command DLQ selection expired.");
            var token = selections.Create(
                attemptId,
                message.MessageId,
                expiresAtUtc,
                digest.CreateImmutableFieldsDigest(command)
            );
            diagnostics.Inspected(state.Classification, command.NotificationType);

            return new(
                command.IdempotencyKey,
                command.NotificationType,
                command.ActionOccurredAtUtc,
                SentAt(message),
                ReceiveCount(message),
                state.Classification,
                UnavailableHistory,
                digest.CreateRecipientDigest(command.EmailAddress),
                state.RecordedAtUtc,
                state.LeaseExpiresAtUtc,
                token
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw InspectionFailure();
        }
    }

    private InvalidOperationException InspectionFailure()
    {
        diagnostics.InspectionFailed();

        return new InvalidOperationException("Command DLQ inspection failed.");
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

    private void EnsureTimely(long started, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(administration.Value.DependencyTimeoutSeconds))
            throw new TimeoutException("Command DLQ inspection exceeded its timeout.");
    }

    private static int? ReceiveCount(Message message) =>
        message.Attributes is not null
        && message.Attributes.TryGetValue("ApproximateReceiveCount", out var count)
        && int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value > 0
            ? value
            : null;

    private static DateTimeOffset? SentAt(Message message)
    {
        if (
            message.Attributes is null
            || !message.Attributes.TryGetValue("SentTimestamp", out var timestamp)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
        )
            return null;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
