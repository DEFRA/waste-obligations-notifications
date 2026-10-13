using Defra.WasteObligations.Consumer.Consumers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Submission;

public sealed class DeclarationHealthCheck(
    IDeclarationClient client,
    IOptions<AnalyticsEventConsumerOptions> options
) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (!options.Value.ProcessingEnabled)
            return HealthCheckResult.Healthy();
        try
        {
            return await client.CheckHealth(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Declaration API unavailable");
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("Declaration API unavailable");
        }
    }
}
