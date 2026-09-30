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
    ILogger<NotificationCommandConsumer> logger
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
        await migrationReadiness.Wait(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await ReceiveCommands(stoppingToken);

                foreach (var message in response.Messages ?? [])
                {
                    var command = NotificationCommandMessageReader.Read(message);
                    metrics.RecordReceived(command.NotificationType);

                    // Ticket 02 adds Notify delivery. These failures retry and can reach the DLQ under queue redrive policy.
                    if (command.ActionOccurredAtUtc >= cutover)
                    {
                        throw new InvalidOperationException(
                            "Notification command delivery after cutover is not yet enabled."
                        );
                    }

                    var result = await recordStoreFactory.GetRecordStore().RecordSuppression(command, stoppingToken);

                    if (result == SuppressionClaimResult.Conflict)
                    {
                        throw new InvalidDataException(
                            "Notification command idempotency key conflicts with an existing command."
                        );
                    }

                    var outcome = NotificationDeliveryOutcome.DeliverySuppressed.ToStorageValue();
                    metrics.RecordOutcome(command.NotificationType, outcome);
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation(
                            "Notification command outcome {Outcome} for {NotificationType} from SQS message {MessageId}",
                            outcome,
                            command.NotificationType,
                            message.MessageId
                        );
                    }
                    await sqsClient.DeleteMessageAsync(options.Value.QueueUrl, message.ReceiptHandle, stoppingToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Notification command consumption failed");
                await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
            }
        }
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

    private DateTimeOffset ReadCutover()
    {
        if (!options.Value.TryReadCutover(out var cutover))
            throw new InvalidOperationException("EmailDeliveryCutoverUtc must be a UTC timestamp.");

        return cutover;
    }
}
