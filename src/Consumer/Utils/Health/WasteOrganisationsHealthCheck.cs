using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Services.WasteOrganisations;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Utils.Health;

public sealed class WasteOrganisationsHealthCheck(
    IOptions<AnalyticsEventConsumerOptions> options,
    IWasteOrganisationsClient client
) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) =>
        AnalyticsDependencyHealthCheck.Check(
            options.Value.ProcessingEnabled,
            client.CheckHealth,
            "WasteOrganisations service unavailable",
            cancellationToken
        );
}
