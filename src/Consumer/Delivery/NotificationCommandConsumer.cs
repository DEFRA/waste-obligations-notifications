using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Delivery;

public sealed class NotificationCommandConsumer(
    IAmazonSQS sqsClient,
    IOptions<NotificationCommandDeliveryOptions> options,
    INotificationDeliveryRecordStoreFactory recordStoreFactory,
    MongoMigrationReadiness migrationReadiness,
    NotificationCommandMetrics metrics,
    ILogger<NotificationCommandConsumer> logger,
    INotifyEmailClient notifyClient,
    INotificationCommandDigest digest
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ProcessingEnabled)
        {
            logger.LogWarning("Notification command consumption is disabled");
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);

            return;
        }

        var cutover = ReadCutover();
        if (!options.Value.HasValidProcessingBudget)
            throw new InvalidOperationException(
                "Notification command lease and visibility do not cover the processing budget."
            );
        await migrationReadiness.Wait(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            string? messageId = null;
            string? notificationType = null;
            string? reference = null;
            try
            {
                // SQS may make a message invisible before its receive response reaches this host.
                var receiveStartedAt = Stopwatch.GetTimestamp();
                var response = await ReceiveCommands(stoppingToken);
                foreach (var message in response.Messages ?? [])
                {
                    messageId = message.MessageId;
                    var command = NotificationCommandMessageReader.Read(message).NormaliseRecipient();
                    notificationType = command.NotificationType;
                    reference = digest.CreateNotifyReference(command.IdempotencyKey);
                    await Process(message, command, reference, cutover, receiveStartedAt, stoppingToken);
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                // Dependency exceptions may contain payload or recipient data. Log a bounded category only.
                LogFailure(exception, messageId, notificationType, reference);
                await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
            }
        }
    }

    private void LogFailure(Exception exception, string? messageId, string? notificationType, string? reference)
    {
        var safeException =
            exception is TimeoutException
                ? new TimeoutException("Notification command dependency exceeded its configured timeout.")
                : null;
        logger.LogError(
            safeException,
            "Notification command consumption failed for {NotificationType} from SQS message {MessageId} with Notify reference {NotifyReference}",
            notificationType,
            messageId,
            reference
        );
    }

    private async Task Process(
        Message message,
        NotificationCommand command,
        string reference,
        DateTimeOffset cutover,
        long receiveStartedAt,
        CancellationToken stoppingToken
    )
    {
        metrics.RecordReceived(command.NotificationType);
        var store = recordStoreFactory.GetRecordStore();
        var outcome = NotificationDeliveryOutcome.DeliverySuppressed.ToStorageValue();
        if (command.ActionOccurredAtUtc < cutover)
        {
            var result = await RunBounded(
                token => store.RecordSuppression(command, token),
                options.Value.ClaimTimeoutSeconds,
                stoppingToken
            );
            if (result is SuppressionClaimResult.Conflict or SuppressionClaimResult.ActiveClaim)
                throw new InvalidOperationException("Notification command suppression is not terminal.");
            if (result == SuppressionClaimResult.TerminalDuplicate)
                outcome = "terminal-duplicate";
        }
        else
        {
            EnsureRemaining(
                receiveStartedAt,
                options.Value.VisibilityTimeoutSeconds,
                options.Value.ClaimTimeoutSeconds + options.Value.CompletionBudgetSeconds
            );
            var claimStartedAt = Stopwatch.GetTimestamp();
            var attemptOwner = Guid.NewGuid().ToString("N");
            var claim = await RunBounded(
                token => store.Claim(command, attemptOwner, options.Value.CommandLeaseSeconds, token),
                options.Value.ClaimTimeoutSeconds,
                stoppingToken
            );
            if (claim == DeliveryClaimResult.TerminalDuplicate)
            {
                outcome = "terminal-duplicate";
            }
            else
            {
                if (claim != DeliveryClaimResult.Claimed)
                    throw new InvalidOperationException("Notification command cannot acquire a delivery claim.");
                EnsureRemaining(
                    receiveStartedAt,
                    options.Value.VisibilityTimeoutSeconds,
                    options.Value.CompletionBudgetSeconds
                );
                EnsureRemaining(
                    claimStartedAt,
                    options.Value.CommandLeaseSeconds,
                    options.Value.CompletionBudgetSeconds
                );
                var acceptance = await RunBounded(
                    token => notifyClient.Send(command, reference, token),
                    options.Value.NotifyTimeoutSeconds,
                    stoppingToken
                );
                EnsureRemaining(
                    claimStartedAt,
                    options.Value.CommandLeaseSeconds,
                    options.Value.AcceptanceTimeoutSeconds
                        + options.Value.DeleteTimeoutSeconds
                        + options.Value.SafetyHeadroomSeconds
                );
                EnsureRemaining(
                    receiveStartedAt,
                    options.Value.VisibilityTimeoutSeconds,
                    options.Value.AcceptanceTimeoutSeconds
                        + options.Value.DeleteTimeoutSeconds
                        + options.Value.SafetyHeadroomSeconds
                );
                var recorded = await RunBounded(
                    token => store.RecordAcceptance(command, attemptOwner, acceptance, token),
                    options.Value.AcceptanceTimeoutSeconds,
                    stoppingToken
                );
                if (!recorded)
                    throw new InvalidOperationException(
                        "Notification command acceptance was not recorded by its current owner."
                    );
                outcome = NotificationDeliveryOutcome.DeliveryAccepted.ToStorageValue();
            }
        }

        metrics.RecordOutcome(command.NotificationType, outcome);
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Notification command outcome {Outcome} for {NotificationType} from SQS message {MessageId} with Notify reference {NotifyReference}",
                outcome,
                command.NotificationType,
                message.MessageId,
                reference
            );
        await RunBounded(
            token => sqsClient.DeleteMessageAsync(options.Value.QueueUrl, message.ReceiptHandle, token),
            options.Value.DeleteTimeoutSeconds,
            stoppingToken
        );
    }

    private async Task<ReceiveMessageResponse> ReceiveCommands(CancellationToken stoppingToken) =>
        await RunBounded(
            token =>
                sqsClient.ReceiveMessageAsync(
                    new ReceiveMessageRequest
                    {
                        QueueUrl = options.Value.QueueUrl,
                        MaxNumberOfMessages = options.Value.BatchSize,
                        MessageAttributeNames = ["All"],
                        WaitTimeSeconds = options.Value.WaitTimeSeconds,
                        VisibilityTimeout = options.Value.VisibilityTimeoutSeconds,
                    },
                    token
                ),
            options.Value.ReceiveTimeoutSeconds,
            stoppingToken
        );

    private static async Task<T> RunBounded<T>(
        Func<CancellationToken, Task<T>> operation,
        int timeoutSeconds,
        CancellationToken stoppingToken
    )
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        source.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var result = await operation(source.Token);
            stoppingToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(startedAt) >= TimeSpan.FromSeconds(timeoutSeconds))
                throw new TimeoutException("Notification command dependency exceeded its configured timeout.");

            return result;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            throw new TimeoutException("Notification command dependency exceeded its configured timeout.");
        }
    }

    private static void EnsureRemaining(long startedAt, int durationSeconds, int remainingBudgetSeconds)
    {
        if (
            Stopwatch.GetElapsedTime(startedAt) + TimeSpan.FromSeconds(remainingBudgetSeconds)
            >= TimeSpan.FromSeconds(durationSeconds)
        )
            throw new TimeoutException(
                "Notification command has insufficient ownership or visibility budget remaining."
            );
    }

    private DateTimeOffset ReadCutover()
    {
        if (!options.Value.TryReadCutover(out var cutover))
            throw new InvalidOperationException("EmailDeliveryCutoverUtc must include an explicit UTC offset.");

        return cutover;
    }
}
