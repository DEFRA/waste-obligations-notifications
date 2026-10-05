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
    NotificationCommandMetrics metrics,
    ILogger<NotificationCommandConsumer> logger
) : StartupBackgroundService(startup)
{
    protected override async Task ExecuteAfterStartup(CancellationToken stoppingToken)
    {
        var cutover = ReadCutover();

        while (!stoppingToken.IsCancellationRequested)
        {
            var failureReason = "queue-error";
            try
            {
                var response = await ReceiveCommands(stoppingToken);

                foreach (var message in response.Messages ?? [])
                {
                    var command = ReadCommand(message, ref failureReason);
                    var notificationType = options.Value.GetDiagnosticNotificationType(command.NotificationType);
                    metrics.RecordReceived(notificationType);

                    failureReason = "store-error";
                    var store = recordStoreFactory.GetRecordStore();
                    var result = await ReadOrRecordSuppression(store, command, cutover, stoppingToken);
                    if (result is null)
                    {
                        // Ticket 02 adds sending; only already-suppressed commands can complete this path.
                        failureReason = "delivery-unavailable";
                        throw new InvalidOperationException(
                            "Notification command delivery after cutover is not yet enabled."
                        );
                    }

                    if (result == SuppressionClaimResult.Conflict)
                    {
                        failureReason = "conflict";
                        throw new InvalidDataException(
                            "Notification command idempotency key conflicts with an existing command."
                        );
                    }

                    var outcome = NotificationDeliveryOutcome.DeliverySuppressed.ToStorageValue();
                    metrics.RecordOutcome(notificationType, outcome);
                    LogOutcome(outcome, notificationType, message.MessageId);
                    failureReason = "queue-error";
                    await sqsClient.DeleteMessageAsync(options.Value.QueueUrl, message.ReceiptHandle, stoppingToken);
                }
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                // A dependency can fail after cancellation; do not expose its exception through host logging.
                return;
            }
            catch (Exception exception)
            {
                LogFailure(failureReason, exception);
                await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
            }
        }
    }

    private static async Task<SuppressionClaimResult?> ReadOrRecordSuppression(
        INotificationDeliveryRecordStore store,
        NotificationCommand command,
        DateTimeOffset? cutover,
        CancellationToken cancellationToken
    )
    {
        if (IsAtOrAfterCutover(command.ActionOccurredAtUtc, cutover))
            return await store.GetSuppression(command, cancellationToken);

        return await store.RecordSuppression(command, cancellationToken);
    }

    private void LogOutcome(string outcome, string notificationType, string messageId)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Notification command outcome {Outcome} for {NotificationType} from SQS message {MessageId}",
                outcome,
                notificationType,
                messageId
            );
    }

    private static NotificationCommand ReadCommand(Message message, ref string failureReason)
    {
        failureReason = "invalid-command";

        return NotificationCommandMessageReader.Read(message);
    }

    private void LogFailure(string failureReason, Exception exception)
    {
        if (logger.IsEnabled(LogLevel.Error))
            logger.LogError(
                "Notification command consumption failed: {FailureReason} ({ExceptionType})",
                exception is TimeoutException ? "processing-timeout" : failureReason,
                exception.GetType().Name
            );
    }

    private async Task<ReceiveMessageResponse> ReceiveCommands(CancellationToken stoppingToken)
    {
        using var receiveCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        receiveCancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(options.Value.ReceiveTimeoutSeconds));

        try
        {
            return await sqsClient.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = options.Value.QueueUrl,
                    MaxNumberOfMessages = options.Value.BatchSize,
                    MessageAttributeNames = ["All"],
                    WaitTimeSeconds = options.Value.WaitTimeSeconds,
                },
                receiveCancellationTokenSource.Token
            );
        }
        catch (OperationCanceledException exception) when (!stoppingToken.IsCancellationRequested)
        {
            throw new TimeoutException("Notification command receive exceeded its configured timeout.", exception);
        }
    }

    private static bool IsAtOrAfterCutover(DateTimeOffset actionOccurredAtUtc, DateTimeOffset? cutover) =>
        cutover is not null && actionOccurredAtUtc >= cutover;

    private DateTimeOffset? ReadCutover()
    {
        if (!options.Value.TryReadCutover(out var cutover))
            throw new InvalidOperationException(
                "EmailDeliveryCutoverUtc must be null or include an explicit UTC offset."
            );

        return cutover;
    }
}
