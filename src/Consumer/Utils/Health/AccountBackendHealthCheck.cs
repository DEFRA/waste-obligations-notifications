using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Utils.Health;

public sealed class AccountBackendHealthCheck(
    IOptions<AnalyticsEventConsumerOptions> options,
    IAccountBackendClient client,
    TimeProvider? timeProvider = null
) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) =>
        AnalyticsDependencyHealthCheck.Check(
            options.Value.ProcessingEnabled,
            client.CheckHealth,
            "AccountBackend service unavailable",
            timeProvider ?? TimeProvider.System,
            cancellationToken
        );
}
