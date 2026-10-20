using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Defra.WasteObligations.Consumer.Utils.Health;

internal static class AnalyticsDependencyHealthCheck
{
    internal static async Task<HealthCheckResult> Check(
        bool processingEnabled,
        Func<CancellationToken, Task> checkHealth,
        string failureDescription,
        CancellationToken cancellationToken
    )
    {
        if (!processingEnabled)
            return HealthCheckResult.Healthy("Analytics processing disabled");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
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
