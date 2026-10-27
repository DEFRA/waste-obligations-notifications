using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Startup;
using Defra.WasteObligations.Consumer.Utils.Metrics;
using Microsoft.Extensions.Options;
using Serilog.Context;

namespace Defra.WasteObligations.Consumer.Delivery;

public sealed class NotificationCommandConsumer(
    IAmazonSQS sqsClient,
    IOptions<NotificationCommandDeliveryOptions> options,
    INotificationDeliveryRecordStoreFactory recordStoreFactory,
    ApplicationStartup startup,
    INotificationCommandMetrics metrics,
    ILogger<NotificationCommandConsumer> logger,
    INotifyEmailClient notifyClient,
    INotificationCommandDigest digest,
    TimeProvider timeProvider,
    INotificationJourneyMetrics journeyMetrics
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
            try
            {
                // SQS may make the message invisible before the receive response reaches this host.
                var receiveStartedAt = Stopwatch.GetTimestamp();
                var messages = await ReceiveCommands(cutover.HasValue, stoppingToken);
                foreach (var message in messages)
                    await ProcessMessage(message, cutover, receiveStartedAt, stoppingToken);
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                // Dependency failures during cancellation must not reach host exception logging.
                return;
            }
            catch (Exception)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
            }
        }
    }

    private async Task ProcessMessage(
        Message message,
        DateTimeOffset? cutover,
        long receiveStartedAt,
        CancellationToken stoppingToken
    )
    {
        IDisposable? traceScope = null;
        string? notificationType = null;
        string? reference = null;
        var stage = NotificationJourneyValues.Stage.Parse;
        try
        {
            var command = ReadCommand(message);
            traceScope = LogContext.PushProperty("TraceId", command.CorrelationId);
            notificationType = options.Value.GetDiagnosticNotificationType(command.NotificationType);
            reference = digest.CreateNotifyReference(command.IdempotencyKey);

            await Process(
                message,
                command,
                reference,
                cutover,
                receiveStartedAt,
                nextStage => stage = nextStage,
                stoppingToken
            );
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            journeyMetrics.RecordFailure(NotificationJourneyValues.Consumer.Delivery, stage);
            // Dependency exceptions may contain payload or recipient data. Log a bounded category only.
            LogFailure(exception, message.MessageId, notificationType, reference);
            throw;
        }
        finally
        {
            traceScope?.Dispose();
        }
    }

    private void LogFailure(Exception exception, string? messageId, string? notificationType, string? reference)
    {
        if (!logger.IsEnabled(LogLevel.Warning))
            return;
        var failure = exception as NotificationCommandProcessingException;
        logger.LogWarning(
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
        Action<string> setStage,
        CancellationToken stoppingToken
    )
    {
        var notificationType = options.Value.GetDiagnosticNotificationType(command.NotificationType);
        metrics.RecordReceived(notificationType);
        var store = recordStoreFactory.GetRecordStore();
        var outcome = NotificationDeliveryOutcome.DeliverySuppressed.ToStorageValue();
        if (cutover is null || command.ActionOccurredAtUtc < cutover)
        {
            setStage(NotificationJourneyValues.Stage.Suppression);
            var result = await RunBounded(
                token => store.RecordSuppression(command, token),
                options.Value.ClaimTimeoutSeconds,
                stoppingToken,
                failureReason: NotificationCommandFailureReason.StoreError
            );
            var suppressedAt = timeProvider.GetUtcNow();

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

            if (result == SuppressionClaimResult.Recorded)
                RecordOutcomeLatency(
                    notificationType,
                    outcome,
                    (suppressedAt - command.ActionOccurredAtUtc).TotalSeconds
                );
        }
        else
            outcome = await ProcessPostCutover(
                command,
                reference,
                notificationType,
                store,
                receiveStartedAt,
                setStage,
                stoppingToken
            );

        metrics.RecordOutcome(notificationType, outcome);
        LogOutcome(message, command, reference, notificationType, outcome, cutover);
        stoppingToken.ThrowIfCancellationRequested();
        setStage(NotificationJourneyValues.Stage.Acknowledge);
        await RunBounded(
            token => sqsClient.DeleteMessageAsync(options.Value.QueueUrl, message.ReceiptHandle, token),
            options.Value.DeleteTimeoutSeconds,
            stoppingToken,
            failureReason: NotificationCommandFailureReason.QueueError
        );
    }

    private void LogOutcome(
        Message message,
        NotificationCommand command,
        string reference,
        string notificationType,
        string outcome,
        DateTimeOffset? cutover
    )
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Notification command outcome {Outcome} ({OutcomeMeaning}) for {NotificationType} from SQS message {MessageId} with Notify reference {NotifyReference}, template {TemplateId}, action {ActionOccurredAtUtc}",
                outcome,
                outcome switch
                {
                    "terminal-duplicate" => "matching terminal evidence; no new request or suppression",
                    _ when outcome == NotificationDeliveryOutcome.DeliveryAccepted.ToStorageValue() =>
                        "email request accepted by Notify; acceptance recorded",
                    _ when cutover is null => "would request this template; suppressed because cutover is unset",
                    _ => "would request this template; suppressed because action precedes cutover",
                },
                notificationType,
                message.MessageId,
                reference,
                Guid.TryParse(command.TemplateId, out _) ? command.TemplateId : null,
                command.ActionOccurredAtUtc
            );
    }

    private async Task<string> ProcessPostCutover(
        NotificationCommand command,
        string reference,
        string notificationType,
        INotificationDeliveryRecordStore store,
        long receiveStartedAt,
        Action<string> setStage,
        CancellationToken stoppingToken
    )
    {
        setStage(NotificationJourneyValues.Stage.ProcessingBudget);
        EnsureRemaining(
            receiveStartedAt,
            options.Value.VisibilityTimeoutSeconds,
            options.Value.ClaimTimeoutSeconds + options.Value.CompletionBudgetSeconds
        );
        var claimStartedAt = Stopwatch.GetTimestamp();
        var attemptOwner = Guid.NewGuid().ToString("N");
        DeliveryClaimResult claim;
        setStage(NotificationJourneyValues.Stage.Claim);
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
        setStage(NotificationJourneyValues.Stage.ProcessingBudget);
        EnsureRemaining(
            receiveStartedAt,
            options.Value.VisibilityTimeoutSeconds,
            options.Value.CompletionBudgetSeconds
        );
        EnsureRemaining(claimStartedAt, options.Value.CommandLeaseSeconds, options.Value.CompletionBudgetSeconds);
        var sendStartedAt = Stopwatch.GetTimestamp();
        NotifyAcceptance acceptance;
        DateTimeOffset acceptedAt;
        setStage(NotificationJourneyValues.Stage.Send);
        try
        {
            acceptance = await RunBounded(
                token => notifyClient.Send(command, reference, token),
                options.Value.NotifyTimeoutSeconds,
                stoppingToken,
                preserveCompletedResult: true,
                failureReason: NotificationCommandFailureReason.NotifyIndeterminate
            );
            acceptedAt = timeProvider.GetUtcNow();

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

        RecordOutcomeLatency(
            notificationType,
            NotificationDeliveryOutcome.DeliveryAccepted.ToStorageValue(),
            (acceptedAt - command.ActionOccurredAtUtc).TotalSeconds
        );

        // A confirmed send still needs durable evidence after timeout, lease expiry or shutdown.
        // Mongo's owner and pending-outcome checks fence a replaced or terminal claim.
        setStage(NotificationJourneyValues.Stage.RecordAcceptance);
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

    private void RecordOutcomeLatency(string notificationType, string outcome, double elapsedSeconds)
    {
        if (elapsedSeconds < 0)
            return;

        try
        {
            metrics.RecordOutcomeLatency(notificationType, outcome, elapsedSeconds);
        }
        catch (Exception exception)
        {
            // Telemetry must not prevent recording a confirmed acceptance or acknowledging suppression.
            LogLatencyMetricFailure(exception);
        }
    }

    private void LogLatencyMetricFailure(Exception exception) =>
        logger.LogError("Notification outcome latency metric failed ({ExceptionType}).", exception.GetType().Name);

    private async Task<Message[]> ReceiveCommands(bool cutoverConfigured, CancellationToken stoppingToken)
    {
        try
        {
            var response = await RunBounded(
                token =>
                    sqsClient.ReceiveMessageAsync(
                        new ReceiveMessageRequest
                        {
                            QueueUrl = options.Value.QueueUrl,
                            // Larger receives require a reviewed visibility/claim budget.
                            MaxNumberOfMessages = 1,
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
            journeyMetrics.RecordPoll(NotificationJourneyValues.Consumer.Delivery);
            journeyMetrics.RecordCutover(cutoverConfigured);

            return (response.Messages ?? []).ToArray();
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            journeyMetrics.RecordFailure(
                NotificationJourneyValues.Consumer.Delivery,
                NotificationJourneyValues.Stage.Receive
            );
            LogFailure(exception, null, null, null);
            throw;
        }
    }

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
