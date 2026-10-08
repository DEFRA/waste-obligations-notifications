using System.Diagnostics;
using System.Globalization;
using System.Net;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Administration;

public sealed class CommandDlqQueueOperations(
    IAmazonSQS sqs,
    IOptions<CommandDlqAdministrationOptions> administration,
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

            return new(visible, inFlight, delayed, checked(visible + inFlight + delayed));
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
