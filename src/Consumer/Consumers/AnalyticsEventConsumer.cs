using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Consumers;

public sealed class AnalyticsEventConsumer(
    IAmazonSQS sqsClient,
    IOptions<AnalyticsEventConsumerOptions> options,
    ILogger<AnalyticsEventConsumer> logger
) : BackgroundService
{
    private const string ContentEncodingHeader = "Content-Encoding";
    private const string GzipBase64ContentEncoding = "gzip+base64";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ProcessingEnabled)
        {
            logger.LogWarning("Analytics event consumption is disabled");
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);

            return;
        }

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
                    // A future PR will add audit-history lookup and notification-command publication before deletion.
                    var analyticsEvent = ReadMessage(message);

                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation(
                            "Consumed analytics event {EventId} for {EntityId}",
                            analyticsEvent.EventId,
                            analyticsEvent.EntityId
                        );
                    }

                    await sqsClient.DeleteMessageAsync(options.Value.QueueUrl, message.ReceiptHandle, stoppingToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Analytics event consumption failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
        }
    }

    private static AnalyticsEvent ReadMessage(Message message)
    {
        using var document = JsonDocument.Parse(ReadBody(message));
        var root = document.RootElement;

        return new AnalyticsEvent(ReadRequiredString(root, "eventId"), ReadRequiredString(root, "entityId"));
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"Analytics event is missing required property '{propertyName}'.");
        }

        return property.GetString()!;
    }

    private static string ReadBody(Message message)
    {
        if (
            message.MessageAttributes is null
            || !message.MessageAttributes.TryGetValue(ContentEncodingHeader, out var contentEncoding)
            || contentEncoding.StringValue is null
        )
        {
            return message.Body;
        }

        if (contentEncoding.StringValue != GzipBase64ContentEncoding)
        {
            throw new InvalidOperationException(
                $"Analytics event message content encoding '{contentEncoding.StringValue}' is not supported."
            );
        }

        var bytes = Convert.FromBase64String(message.Body);
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        return reader.ReadToEnd();
    }

    private sealed record AnalyticsEvent(string EventId, string EntityId);
}
