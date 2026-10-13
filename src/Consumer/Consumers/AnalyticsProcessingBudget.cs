using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Consumers;

public sealed class AnalyticsProcessingBudget(IOptions<AnalyticsEventConsumerOptions> options)
{
    public void EnsureRemaining(long receiveStartedAt, int remainingSeconds)
    {
        if (
            Stopwatch.GetElapsedTime(receiveStartedAt) + TimeSpan.FromSeconds(remainingSeconds)
            >= TimeSpan.FromSeconds(options.Value.VisibilityTimeoutSeconds)
        )
            throw new TimeoutException("Analytics processing budget exhausted.");
    }

    public static async Task<T> Run<T>(
        Func<CancellationToken, Task<T>> operation,
        int timeoutSeconds,
        CancellationToken stoppingToken
    )
    {
        stoppingToken.ThrowIfCancellationRequested();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        source.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var startedAt = Stopwatch.GetTimestamp();
        var result = await operation(source.Token);
        if (
            source.IsCancellationRequested
            || Stopwatch.GetElapsedTime(startedAt) >= TimeSpan.FromSeconds(timeoutSeconds)
        )
        {
            if (result is IDisposable disposable)
                disposable.Dispose();
            stoppingToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Analytics dependency exceeded its deadline.");
        }

        return result;
    }
}
