using Defra.WasteObligations.Consumer.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Defra.WasteObligations.Consumer.Utils.Health;

public sealed class MongoMigrationCompletionHealthCheck(MongoMigrationCompletion completion) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            completion.IsCompleted
                ? HealthCheckResult.Healthy("Critical Mongo migrations completed.")
                : HealthCheckResult.Unhealthy("Critical Mongo migrations have not completed.")
        );
}
