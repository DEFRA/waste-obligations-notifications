using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Delivery;

public sealed class NotificationCommandConsumer(
    IAmazonSQS sqsClient,
    IOptions<NotificationCommandDeliveryOptions> options,
    INotificationDeliveryRecordStoreFactory recordStoreFactory,
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

        var cutover = ReadCutover(options.Value.EmailDeliveryCutoverUtc);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await sqsClient.ReceiveMessageAsync(
                    new ReceiveMessageRequest
                    {
                        QueueUrl = options.Value.QueueUrl,
                        MaxNumberOfMessages = options.Value.BatchSize,
                        MessageAttributeNames = ["All"],
                        WaitTimeSeconds = options.Value.WaitTimeSeconds,
                    },
                    stoppingToken
                );

                foreach (var message in response.Messages ?? [])
                {
                    var command = NotificationCommandMessageReader.Read(message);
                    metrics.RecordReceived(command.NotificationType);

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
                    logger.LogInformation(
                        "Notification command outcome {Outcome} for {NotificationType} from SQS message {MessageId}",
                        outcome,
                        command.NotificationType,
                        message.MessageId
                    );
                    await sqsClient.DeleteMessageAsync(options.Value.QueueUrl, message.ReceiptHandle, stoppingToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Notification command consumption failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
        }
    }

    private static DateTimeOffset ReadCutover(string configuredCutover)
    {
        if (
            !DateTimeOffset.TryParse(
                configuredCutover,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var cutover
            )
            || cutover.Offset != TimeSpan.Zero
        )
        {
            throw new InvalidOperationException("EmailDeliveryCutoverUtc must be a UTC timestamp.");
        }

        return cutover;
    }
}
