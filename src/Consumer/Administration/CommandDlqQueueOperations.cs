using System.Diagnostics;
using System.Globalization;
using System.Net;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Administration;

public sealed class CommandDlqQueueOperations(
    IAmazonSQS sqs,
    IOptions<CommandDlqAdministrationOptions> administration,
    IOptions<NotificationCommandDeliveryOptions> delivery,
    CommandDlqDiagnostics diagnostics
)
{
    public async Task<CommandDlqStatus> GetStatus(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(administration.Value.DependencyTimeoutSeconds));
        try
        {
            EnsureTimely(started, source.Token);
            var response = await sqs.GetQueueAttributesAsync(
                new GetQueueAttributesRequest
                {
                    QueueUrl = administration.Value.QueueUrl,
                    AttributeNames =
                    [
                        "ApproximateNumberOfMessages",
                        "ApproximateNumberOfMessagesNotVisible",
                        "ApproximateNumberOfMessagesDelayed",
                        "QueueArn",
                    ],
                },
                source.Token
            );
            EnsureTimely(started, source.Token);
            if (response.HttpStatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("Command DLQ attributes were not confirmed.");
            var visible = ReadCount(response, "ApproximateNumberOfMessages");
            var inFlight = ReadCount(response, "ApproximateNumberOfMessagesNotVisible");
            var delayed = ReadCount(response, "ApproximateNumberOfMessagesDelayed");
            var tasks = await sqs.ListMessageMoveTasksAsync(
                new ListMessageMoveTasksRequest { SourceArn = ReadArn(response), MaxResults = 1 },
                source.Token
            );
            EnsureTimely(started, source.Token);
            if (tasks.HttpStatusCode != HttpStatusCode.OK || tasks.Results is { Count: > 1 })
                throw new InvalidOperationException("Command DLQ redrive status was not confirmed.");
            var task = tasks.Results?.SingleOrDefault();

            return new(
                visible,
                inFlight,
                delayed,
                checked(visible + inFlight + delayed),
                task is null ? null : ReadTask(task)
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            diagnostics.StatusFailed();

            throw new InvalidOperationException("Command DLQ status failed.");
        }
    }

    public async Task<CommandDlqRedriveTaskStarted> RedriveAll(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(administration.Value.DependencyTimeoutSeconds));
        try
        {
            var sourceArn = await GetArn(administration.Value.QueueUrl, started, source.Token);
            var destinationArn = await GetArn(delivery.Value.QueueUrl, started, source.Token);
            if (sourceArn == destinationArn)
                throw new InvalidOperationException("Command DLQ and destination must be separate queues.");
            EnsureTimely(started, source.Token);
            var response = await sqs.StartMessageMoveTaskAsync(
                new StartMessageMoveTaskRequest { SourceArn = sourceArn, DestinationArn = destinationArn },
                source.Token
            );
            EnsureTimely(started, source.Token);
            if (response.HttpStatusCode != HttpStatusCode.OK || string.IsNullOrWhiteSpace(response.TaskHandle))
                throw new InvalidOperationException("Command DLQ redrive task was not confirmed.");
            diagnostics.RedriveAllStarted();

            return new(response.TaskHandle);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            diagnostics.RedriveAllFailed();

            throw new InvalidOperationException("Command DLQ whole-queue redrive failed.");
        }
    }

    private async Task<string> GetArn(string queueUrl, long started, CancellationToken token)
    {
        EnsureTimely(started, token);
        var response = await sqs.GetQueueAttributesAsync(
            new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] },
            token
        );
        EnsureTimely(started, token);
        if (response.HttpStatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException("Command DLQ queue identity was not confirmed.");

        return ReadArn(response);
    }

    private static string ReadArn(GetQueueAttributesResponse response)
    {
        if (
            response.Attributes is not null
            && response.Attributes.TryGetValue("QueueArn", out var arn)
            && !string.IsNullOrWhiteSpace(arn)
        )
            return arn;

        throw new InvalidOperationException("Command DLQ queue identity is unavailable.");
    }

    private static CommandDlqRedriveTask ReadTask(ListMessageMoveTasksResultEntry task)
    {
        if (
            task.Status is not ("RUNNING" or "COMPLETED" or "CANCELLING" or "CANCELLED" or "FAILED")
            || task.ApproximateNumberOfMessagesMoved < 0
            || task.ApproximateNumberOfMessagesToMove < 0
        )
            throw new InvalidOperationException("Command DLQ redrive task status is invalid.");
        DateTimeOffset? started = task.StartedTimestamp is { } timestamp
            ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
            : null;

        return new(task.Status, task.ApproximateNumberOfMessagesMoved, task.ApproximateNumberOfMessagesToMove, started);
    }

    private void EnsureTimely(long started, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(administration.Value.DependencyTimeoutSeconds))
            throw new TimeoutException("Command DLQ operation exceeded its deadline.");
    }

    private static long ReadCount(GetQueueAttributesResponse response, string name)
    {
        if (
            response.Attributes is not null
            && response.Attributes.TryGetValue(name, out var text)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            && count >= 0
        )
            return count;

        throw new InvalidOperationException("Command DLQ count is unavailable.");
    }
}
