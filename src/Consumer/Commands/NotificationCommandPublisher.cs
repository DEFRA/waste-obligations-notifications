using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Commands;

public sealed class NotificationCommandPublisher(
    IAmazonSQS sqsClient,
    IOptions<NotificationCommandDeliveryOptions> options,
    INotificationCommandDigest digest
) : INotificationCommandPublisher
{
    private static readonly JsonSerializerOptions s_jsonSerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task Publish(NotificationCommand command, CancellationToken cancellationToken)
    {
        command.Validate();
        var normalisedCommand = command.NormaliseRecipient();
        var response = await sqsClient.SendMessageAsync(
            new SendMessageRequest
            {
                QueueUrl = options.Value.QueueUrl,
                MessageBody = JsonSerializer.Serialize(normalisedCommand, s_jsonSerializerOptions),
                MessageDeduplicationId = normalisedCommand.IdempotencyKey,
                MessageGroupId = digest.CreateRecipientLane(normalisedCommand.EmailAddress),
            },
            cancellationToken
        );

        if (response.HttpStatusCode is not System.Net.HttpStatusCode.OK)
        {
            throw new InvalidOperationException("Notification command was not accepted by the command queue.");
        }
    }
}
