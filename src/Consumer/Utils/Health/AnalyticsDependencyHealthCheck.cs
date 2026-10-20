using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Defra.WasteObligations.Consumer.Utils.Health;

internal static class AnalyticsDependencyHealthCheck
{
    internal static async Task<HealthCheckResult> Check(
        bool processingEnabled,
        Func<CancellationToken, Task> checkHealth,
        string failureDescription,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        if (!processingEnabled)
            return HealthCheckResult.Healthy("Analytics processing disabled");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await checkHealth(deadline.Token).WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();

            return HealthCheckResult.Healthy();
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy(failureDescription);
        }
    }
}
