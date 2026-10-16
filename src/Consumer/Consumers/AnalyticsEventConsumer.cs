using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Startup;
using Defra.WasteObligations.Consumer.Submission;
using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace Defra.WasteObligations.Consumer.Consumers;

public sealed class AnalyticsEventConsumer(
    IAmazonSQS sqsClient,
    IOptions<AnalyticsEventConsumerOptions> options,
    ILogger<AnalyticsEventConsumer> logger,
    ApplicationStartup startup,
    SubmittedEmailProducer producer,
    INotificationCommandPublisher publisher,
    INotificationCommandDigest digest
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

        if (!options.Value.HasValidProcessingBudget)
            throw new InvalidOperationException("Analytics visibility does not cover its processing budget.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var receivedAt = Stopwatch.GetTimestamp();
                var response = await ReceiveEvents(stoppingToken);
                foreach (var message in response.Messages ?? [])
                    await ProcessMessage(message, receivedAt, stoppingToken);
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
            }
        }
    }

    private async Task<ReceiveMessageResponse> ReceiveEvents(CancellationToken stoppingToken)
    {
        try
        {
            return await RunBounded(
                token =>
                    sqsClient.ReceiveMessageAsync(
                        new ReceiveMessageRequest
                        {
                            QueueUrl = options.Value.QueueUrl,
                            // One read, publish and delete budget per receive; batching requires review.
                            MaxNumberOfMessages = 1,
                            VisibilityTimeout = options.Value.VisibilityTimeoutSeconds,
                            MessageAttributeNames = ["All"],
                            WaitTimeSeconds = options.Value.WaitTimeSeconds,
                        },
                        token
                    ),
                options.Value.ReceiveTimeoutSeconds,
                stoppingToken
            );
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            LogFailure(exception);
            LogRetry(exception, "receive", false, null, null, null);
            throw;
        }
    }

    private async Task ProcessMessage(Message message, long receivedAt, CancellationToken stoppingToken)
    {
        string? eventId = null;
        string? entityId = null;
        string? messageId = null;
        var stage = "parse";
        var published = false;
        try
        {
            messageId = SafeTransportCorrelation(message.MessageId);
            var analyticsEvent = AnalyticsEvent.Parse(ReadBody(message));
            eventId = SafeEventId(analyticsEvent.EventId) ?? OpaqueCorrelation(analyticsEvent.EventId);
            var declarationEntityId = SafeEntityId(analyticsEvent.EntityId);
            entityId = declarationEntityId ?? OpaqueCorrelation(analyticsEvent.EntityId);

            if (analyticsEvent.IsSubmission)
            {
                stage = "validate-event";
                if (
                    declarationEntityId is null
                    || !analyticsEvent.SupportsSubmissionSchema
                    || analyticsEvent.Version is not > 0
                    || analyticsEvent.OrganisationId is null
                    || analyticsEvent.OrganisationId == Guid.Empty
                )
                    throw new SubmissionProcessingException("invalid-event-data");

                var id = analyticsEvent.EntityId;
                var version = analyticsEvent.Version.Value;
                var organisationId = analyticsEvent.OrganisationId.Value;

                EnsureRemaining(
                    receivedAt,
                    options.Value.ReadTimeoutSeconds
                        + options.Value.PublishTimeoutSeconds
                        + options.Value.DeleteTimeoutSeconds
                );
                // The enclosing catch reads this stage when the awaited read throws, before the next assignment.
#pragma warning disable S1854 // Sonar does not account for this exceptional path through the awaited call.
                stage = "read-declaration";
#pragma warning restore S1854
                var command = await RunBounded(
                    token => producer.Create(organisationId, id[5..].ToLowerInvariant(), version, token),
                    options.Value.ReadTimeoutSeconds,
                    stoppingToken
                );

                EnsureRemaining(receivedAt, options.Value.PublishTimeoutSeconds + options.Value.DeleteTimeoutSeconds);
                stage = "publish-command";
                await RunBounded(
                    async token =>
                    {
                        await publisher.Publish(command, token);
                        return true;
                    },
                    options.Value.PublishTimeoutSeconds,
                    stoppingToken
                );
                published = true;

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "Published notification command for event {EventId} entity {EntityId} with Notify reference {NotifyReference}, type {NotificationType}, template {TemplateId}, action {ActionOccurredAtUtc}",
                        eventId,
                        entityId,
                        digest.CreateNotifyReference(command.IdempotencyKey),
                        command.NotificationType,
                        command.TemplateId,
                        command.ActionOccurredAtUtc
                    );
                }
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Consumed analytics event {EventId} for {EntityId}", eventId, entityId);
            }

            stage = "acknowledge";
            EnsureRemaining(receivedAt, options.Value.DeleteTimeoutSeconds);
            await RunBounded(
                token => sqsClient.DeleteMessageAsync(options.Value.QueueUrl, message.ReceiptHandle, token),
                options.Value.DeleteTimeoutSeconds,
                stoppingToken
            );
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            LogFailure(exception);
            LogRetry(exception, stage, published, messageId, eventId, entityId);
            throw;
        }
    }

    private void LogFailure(Exception exception)
    {
        // Dependency exception text can contain personal data.
        logger.LogError("Analytics event consumption failed ({ExceptionType})", exception.GetType().Name);
    }

    private void LogRetry(
        Exception exception,
        string stage,
        bool published,
        string? messageId,
        string? eventId,
        string? entityId
    )
    {
        var reason = exception switch
        {
            SubmissionProcessingException failure => failure.Reason,
            TimeoutException or OperationCanceledException or TimeoutRejectedException => "dependency-timeout",
            _ when stage == "publish-command" => "unconfirmed-publication",
            _ when stage is "parse" or "validate-event" => "invalid-event-data",
            _ => "dependency-failure",
        };
        var acknowledgementStatus = (stage, published) switch
        {
            ("acknowledge", true) => "command publication confirmed; acknowledgement uncertain; possible queue retry",
            ("acknowledge", false) => "acknowledgement uncertain; possible queue retry",
            _ => "not acknowledged; left for queue retry/recovery",
        };
        logger.LogWarning(
            "Analytics processing at {Stage} failed ({FailureReason}) for SQS message {MessageId}, event {EventId}, entity {EntityId}: {AcknowledgementStatus}",
            stage,
            reason,
            messageId,
            eventId,
            entityId,
            acknowledgementStatus
        );
    }

    private static string? SafeEventId(string value) =>
        value.Length == 26
        && value[0] <= '7'
        && value.All(character => "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(character))
            ? value
            : null;

    private static string? SafeEntityId(string value) =>
        value.Length == 29
        && value.StartsWith("cdec_", StringComparison.Ordinal)
        && value[5..].All(char.IsAsciiHexDigit)
            ? value
            : null;

    private static string? SafeTransportCorrelation(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        if (Guid.TryParseExact(value, "D", out var id))
            return id.ToString("D");

        return OpaqueCorrelation(value);
    }

    private static string OpaqueCorrelation(string value) =>
        "opaque-"
        + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private void EnsureRemaining(long receivedAt, int budgetSeconds)
    {
        if (
            Stopwatch.GetElapsedTime(receivedAt)
                + TimeSpan.FromSeconds(budgetSeconds + options.Value.SafetyHeadroomSeconds)
            >= TimeSpan.FromSeconds(options.Value.VisibilityTimeoutSeconds)
        )
            throw new SubmissionProcessingException("processing-budget-exhausted");
    }

    private static async Task<T> RunBounded<T>(
        Func<CancellationToken, Task<T>> operation,
        int seconds,
        CancellationToken stoppingToken
    )
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        source.CancelAfter(TimeSpan.FromSeconds(seconds));
        var startedAt = Stopwatch.GetTimestamp();
        var result = await operation(source.Token).WaitAsync(source.Token);
        stoppingToken.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(startedAt) >= TimeSpan.FromSeconds(seconds))
            throw new TimeoutException("Analytics dependency exceeded its timeout.");

        return result;
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
}
