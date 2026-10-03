using System.Globalization;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Utils.Health;

public sealed class EmailDeliveryCutoverHealthCheck(IOptions<NotificationCommandDeliveryOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        options.Value.TryReadCutover(out var cutover);
        var mode = cutover is null ? "suppress-all" : "boundary";

        return Task.FromResult(
            HealthCheckResult.Healthy(
                data: new Dictionary<string, object>
                {
                    ["emailDeliveryCutoverUtc"] = cutover?.ToString("O", CultureInfo.InvariantCulture)!,
                    ["mode"] = mode,
                }
            )
        );
    }
}
