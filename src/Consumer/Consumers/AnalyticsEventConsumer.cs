using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Startup;
using Defra.WasteObligations.Consumer.Submission;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Consumers;

public sealed class AnalyticsEventConsumer(
    IAmazonSQS sqsClient,
    IOptions<AnalyticsEventConsumerOptions> options,
    ILogger<AnalyticsEventConsumer> logger,
    ApplicationStartup startup,
    IDeclarationClient declarationClient,
    SubmissionCommandMapper mapper,
    INotificationCommandPublisher publisher,
    INotificationCommandDigest digest,
    AnalyticsProcessingBudget budget
) : StartupBackgroundService(startup)
{
    private const string ContentEncodingHeader = "Content-Encoding";
    private const string GzipBase64ContentEncoding = "gzip+base64";

    protected override async Task ExecuteAfterStartup(CancellationToken stoppingToken)
    {
        if (!options.Value.ProcessingEnabled)
        {
            logger.LogWarning("Analytics event consumption is disabled");
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);

            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var stage = "receive";
            string? messageId = null;
            string? eventId = null;
            string? entityId = null;
            var published = false;
            try
            {
                // The analytics visibility budget covers one event, independently of command delivery and other hosts.
                // Future batching requires review of this complete receive/read/publish/delete budget.
                var receiveStartedAt = Stopwatch.GetTimestamp();
                var response = await AnalyticsProcessingBudget.Run(
                    token => sqsClient.ReceiveMessageAsync(
                        new ReceiveMessageRequest
                        {
                            QueueUrl = options.Value.QueueUrl,
                            MaxNumberOfMessages = 1,
                            MessageAttributeNames = ["All"],
                            WaitTimeSeconds = options.Value.WaitTimeSeconds,
                            VisibilityTimeout = options.Value.VisibilityTimeoutSeconds,
                        },
                        token
                    ),
                    options.Value.ReceiveTimeoutSeconds,
                    stoppingToken
                );

                foreach (var message in response.Messages ?? [])
                {
                    messageId = Guid.TryParseExact(message.MessageId, "D", out _) ? message.MessageId : null;
                    stage = "validation";
                    using var document = JsonDocument.Parse(ReadBody(message));
                    var root = document.RootElement;
                    var rawEventId = ReadRequiredString(root, "eventId");
                    eventId = SubmissionEvent.IsEventId(rawEventId) ? rawEventId : null;
                    var rawEntityId = ReadRequiredString(root, "entityId");
                    entityId = SubmissionEvent.IsEntityId(rawEntityId) ? rawEntityId : null;
                    var submission = SubmissionEvent.Read(root, rawEventId, rawEntityId);
                    if (submission is not null)
                    {
                        stage = "declaration-read";
                        budget.EnsureRemaining(receiveStartedAt, options.Value.CompletionBudgetSeconds);
                        using var declaration = await AnalyticsProcessingBudget.Run(
                            token => declarationClient.Read(submission, token),
                            options.Value.ReadTimeoutSeconds,
                            stoppingToken
                        );
                        stage = "declaration-validation";
                        var command = mapper.Map(submission, declaration.RootElement);
                        stage = "publication";
                        budget.EnsureRemaining(
                            receiveStartedAt,
                            options.Value.PublishTimeoutSeconds
                                + options.Value.DeleteTimeoutSeconds
                                + options.Value.SafetyHeadroomSeconds
                        );
                        await AnalyticsProcessingBudget.Run(
                            async token =>
                            {
                                await publisher.Publish(command, token);

                                return true;
                            },
                            options.Value.PublishTimeoutSeconds,
                            stoppingToken
                        );
                        published = true;
                        logger.LogInformation(
                            "Published submitted email command for analytics event {EventId} for {EntityId} from SQS message {MessageId} with Notify reference {NotifyReference}, template {TemplateId}, type {NotificationType} and action {ActionOccurredAtUtc}",
                            eventId,
                            entityId,
                            messageId,
                            digest.CreateNotifyReference(command.IdempotencyKey),
                            command.TemplateId,
                            command.NotificationType,
                            command.ActionOccurredAtUtc
                        );
                    }

                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation(
                            "Consumed analytics event {EventId} for {EntityId}",
                            eventId,
                            entityId
                        );
                    }

                    stage = "acknowledgement";
                    budget.EnsureRemaining(
                        receiveStartedAt,
                        options.Value.DeleteTimeoutSeconds + options.Value.SafetyHeadroomSeconds
                    );
                    await AnalyticsProcessingBudget.Run(
                        token => sqsClient.DeleteMessageAsync(options.Value.QueueUrl, message.ReceiptHandle, token),
                        options.Value.DeleteTimeoutSeconds,
                        stoppingToken
                    );
                }
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError("Analytics event consumption failed");
                var reason = exception switch
                {
                    SubmissionProcessingException failure => failure.Reason,
                    OperationCanceledException or TimeoutException => "dependency-timeout",
                    JsonException => "invalid-data",
                    _ => stage switch
                    {
                        "publication" => "publication-unconfirmed",
                        "acknowledgement" => "acknowledgement-unconfirmed",
                        "validation" or "declaration-validation" => "invalid-data",
                        _ => "dependency-failed",
                    },
                };
                if (published)
                    logger.LogWarning(
                        "Analytics command publication confirmed but acknowledgement unconfirmed; queue retry is possible at {Stage} for {FailureReason} from SQS message {MessageId}, event {EventId}, entity {EntityId}",
                        stage,
                        reason,
                        messageId,
                        eventId,
                        entityId
                    );
                else
                    logger.LogWarning(
                        "Analytics processing not acknowledged; left for queue retry/recovery at {Stage} for {FailureReason} from SQS message {MessageId}, event {EventId}, entity {EntityId}",
                        stage,
                        reason,
                        messageId,
                        eventId,
                        entityId
                    );
                await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
            }
        }
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        var value = SubmissionEvent.ReadString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Analytics event is missing required property '{propertyName}'.");
        }

        return value;
    }

    private static string ReadBody(Message message)
    {
        if (
            message.MessageAttributes is null
            || !message.MessageAttributes.TryGetValue(ContentEncodingHeader, out var contentEncoding)
        )
        {
            return message.Body;
        }

        if (contentEncoding.StringValue != GzipBase64ContentEncoding)
        {
            throw new SubmissionProcessingException("unsupported-encoding");
        }

        var bytes = Convert.FromBase64String(message.Body);
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        return reader.ReadToEnd();
    }
}
