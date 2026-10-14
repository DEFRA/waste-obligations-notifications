using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Submission;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Utils.Health;

public sealed class DeclarationReadHealthCheck(
    IOptions<AnalyticsEventConsumerOptions> options,
    IDeclarationReadClient client
) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (!options.Value.ProcessingEnabled)
            return HealthCheckResult.Healthy("Analytics processing disabled");
        try
        {
            await client.CheckHealth(cancellationToken);

            return HealthCheckResult.Healthy();
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("Declaration read service unavailable");
        }
    }
}
