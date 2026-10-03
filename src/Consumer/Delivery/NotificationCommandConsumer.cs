using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Startup;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Delivery;

public sealed class NotificationCommandConsumer(
    IAmazonSQS sqsClient,
    IOptions<NotificationCommandDeliveryOptions> options,
    INotificationDeliveryRecordStoreFactory recordStoreFactory,
    ApplicationStartup startup,
    INotificationCommandMetrics metrics,
    ILogger<NotificationCommandConsumer> logger,
    INotifyEmailClient notifyClient,
    INotificationCommandDigest digest
) : StartupBackgroundService(startup)
{
    protected override async Task ExecuteAfterStartup(CancellationToken stoppingToken)
    {
        var cutover = ReadCutover();
        if (!options.Value.HasValidProcessingBudget)
            throw new InvalidOperationException(
                "Notification command lease and visibility do not cover the processing budget."
            );

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
                    var command = ReadCommand(message);
                    notificationType = options.Value.GetDiagnosticNotificationType(command.NotificationType);
                    reference = digest.CreateNotifyReference(command.IdempotencyKey);
                    await Process(message, command, reference, cutover, receiveStartedAt, stoppingToken);
                }
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                // Dependency failures after cancellation must not reach host exception logging.
                return;
            }
            catch (Exception exception)
            {
                // Dependency exceptions may contain payload or recipient data. Log a bounded category only.
                LogFailure(exception, messageId, notificationType, reference);
                await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
            }
        }
    }

    private void LogFailure(Exception exception, string? messageId, string? notificationType, string? reference)
    {
        if (!logger.IsEnabled(LogLevel.Error))
            return;
        var failure = exception as NotificationCommandProcessingException;
        logger.LogError(
            "Notification command consumption failed for {NotificationType} from SQS message {MessageId} with Notify reference {NotifyReference}: {FailureReason} ({ExceptionType})",
            notificationType,
            messageId,
            reference,
            failure?.DiagnosticReason ?? "unexpected-error",
            failure?.ExceptionType ?? exception.GetBaseException().GetType().Name
        );
    }

    private static NotificationCommand ReadCommand(Message message)
    {
        try
        {
            return NotificationCommandMessageReader.Read(message).NormaliseRecipient();
        }
        catch (Exception exception)
        {
            throw new NotificationCommandProcessingException(
                NotificationCommandFailureReason.InvalidCommand,
                exception
            );
        }
    }

    private async Task Process(
        Message message,
        NotificationCommand command,
        string reference,
        DateTimeOffset? cutover,
        long receiveStartedAt,
        CancellationToken stoppingToken
    )
    {
        var notificationType = options.Value.GetDiagnosticNotificationType(command.NotificationType);
        metrics.RecordReceived(notificationType);
        var store = recordStoreFactory.GetRecordStore();
        var outcome = NotificationDeliveryOutcome.DeliverySuppressed.ToStorageValue();
        if (cutover is null || command.ActionOccurredAtUtc < cutover)
        {
            var result = await RunBounded(
                token => store.RecordSuppression(command, token),
                options.Value.ClaimTimeoutSeconds,
                stoppingToken,
                failureReason: NotificationCommandFailureReason.StoreError
            );
            if (result is SuppressionClaimResult.Conflict or SuppressionClaimResult.ActiveClaim)
                throw new NotificationCommandProcessingException(
                    result == SuppressionClaimResult.Conflict
                        ? NotificationCommandFailureReason.Conflict
                        : NotificationCommandFailureReason.ActiveClaim
                );
            if (result == SuppressionClaimResult.TerminalDuplicate)
            {
                metrics.RecordDuplicate(notificationType);
                outcome = "terminal-duplicate";
            }
        }
        else
            outcome = await ProcessPostCutover(
                command,
                reference,
                notificationType,
                store,
                receiveStartedAt,
                stoppingToken
            );

        metrics.RecordOutcome(notificationType, outcome);
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Notification command outcome {Outcome} for {NotificationType} from SQS message {MessageId} with Notify reference {NotifyReference}",
                outcome,
                notificationType,
                message.MessageId,
                reference
            );
        stoppingToken.ThrowIfCancellationRequested();
        await RunBounded(
            token => sqsClient.DeleteMessageAsync(options.Value.QueueUrl, message.ReceiptHandle, token),
            options.Value.DeleteTimeoutSeconds,
            stoppingToken,
            failureReason: NotificationCommandFailureReason.QueueError
        );
    }

    private async Task<string> ProcessPostCutover(
        NotificationCommand command,
        string reference,
        string notificationType,
        INotificationDeliveryRecordStore store,
        long receiveStartedAt,
        CancellationToken stoppingToken
    )
    {
        EnsureRemaining(
            receiveStartedAt,
            options.Value.VisibilityTimeoutSeconds,
            options.Value.ClaimTimeoutSeconds + options.Value.CompletionBudgetSeconds
        );
        var claimStartedAt = Stopwatch.GetTimestamp();
        var attemptOwner = Guid.NewGuid().ToString("N");
        DeliveryClaimResult claim;
        try
        {
            claim = await RunBounded(
                token => store.Claim(command, attemptOwner, options.Value.CommandLeaseSeconds, token),
                options.Value.ClaimTimeoutSeconds,
                stoppingToken,
                failureReason: NotificationCommandFailureReason.StoreError
            );
        }
        catch
        {
            metrics.RecordClaimFailure(notificationType, Stopwatch.GetElapsedTime(claimStartedAt).TotalMilliseconds);
            throw;
        }
        metrics.RecordClaim(notificationType, claim, Stopwatch.GetElapsedTime(claimStartedAt).TotalMilliseconds);
        if (claim == DeliveryClaimResult.TerminalDuplicate)
        {
            metrics.RecordDuplicate(notificationType);

            return "terminal-duplicate";
        }

        if (claim != DeliveryClaimResult.Claimed)
            throw new NotificationCommandProcessingException(
                claim switch
                {
                    DeliveryClaimResult.Conflict => NotificationCommandFailureReason.Conflict,
                    DeliveryClaimResult.ActiveClaim => NotificationCommandFailureReason.ActiveClaim,
                    _ => NotificationCommandFailureReason.StoreError,
                }
            );
        EnsureRemaining(
            receiveStartedAt,
            options.Value.VisibilityTimeoutSeconds,
            options.Value.CompletionBudgetSeconds
        );
        EnsureRemaining(claimStartedAt, options.Value.CommandLeaseSeconds, options.Value.CompletionBudgetSeconds);
        var sendStartedAt = Stopwatch.GetTimestamp();
        NotifyAcceptance acceptance;
        try
        {
            acceptance = await RunBounded(
                token => notifyClient.Send(command, reference, token),
                options.Value.NotifyTimeoutSeconds,
                stoppingToken,
                preserveCompletedResult: true,
                failureReason: NotificationCommandFailureReason.NotifyIndeterminate
            );
            metrics.RecordSendAccepted(notificationType);
        }
        catch
        {
            metrics.RecordSendFailure(notificationType);
            throw;
        }
        finally
        {
            metrics.RecordSendDuration(notificationType, Stopwatch.GetElapsedTime(sendStartedAt).TotalMilliseconds);
        }
        // A confirmed send still needs durable evidence after timeout, lease expiry or shutdown.
        // Mongo's owner and pending-outcome checks fence a replaced or terminal claim.
        var recorded = await RunBounded(
            token => store.RecordAcceptance(command, attemptOwner, acceptance, token),
            options.Value.AcceptanceTimeoutSeconds,
            CancellationToken.None,
            failureReason: NotificationCommandFailureReason.StoreError
        );
        if (!recorded)
            throw new NotificationCommandProcessingException(NotificationCommandFailureReason.OwnershipLost);

        return NotificationDeliveryOutcome.DeliveryAccepted.ToStorageValue();
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
            stoppingToken,
            failureReason: NotificationCommandFailureReason.QueueError
        );

    private static async Task<T> RunBounded<T>(
        Func<CancellationToken, Task<T>> operation,
        int timeoutSeconds,
        CancellationToken stoppingToken,
        bool preserveCompletedResult = false,
        NotificationCommandFailureReason failureReason = NotificationCommandFailureReason.UnexpectedError
    )
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        source.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var result = await operation(source.Token);
            if (!preserveCompletedResult)
            {
                stoppingToken.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(startedAt) >= TimeSpan.FromSeconds(timeoutSeconds))
                    throw new TimeoutException("Notification command dependency exceeded its configured timeout.");
            }

            return result;
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            if (exception is NotificationCommandProcessingException)
                throw;
            var cause =
                exception is OperationCanceledException
                    ? new TimeoutException("Notification command dependency exceeded its configured timeout.")
                    : exception;
            throw new NotificationCommandProcessingException(failureReason, cause);
        }
    }

    private static void EnsureRemaining(long startedAt, int durationSeconds, int remainingBudgetSeconds)
    {
        if (
            Stopwatch.GetElapsedTime(startedAt) + TimeSpan.FromSeconds(remainingBudgetSeconds)
            >= TimeSpan.FromSeconds(durationSeconds)
        )
            throw new NotificationCommandProcessingException(
                NotificationCommandFailureReason.ProcessingTimeout,
                new TimeoutException("Notification command has insufficient ownership or visibility budget remaining.")
            );
    }

    private DateTimeOffset? ReadCutover()
    {
        if (!options.Value.TryReadCutover(out var cutover))
            throw new InvalidOperationException(
                "EmailDeliveryCutoverUtc must be null or include an explicit UTC offset."
            );

        return cutover;
    }
}
