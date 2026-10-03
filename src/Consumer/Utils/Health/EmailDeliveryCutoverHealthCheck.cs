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
        var delivery = options.Value;
        var valid = delivery.TryReadCutover(out var cutover);
        var mode = (valid, cutover) switch
        {
            (false, _) => "invalid",
            (_, null) => "suppress-all",
            _ => "boundary",
        };

        return Task.FromResult(
            HealthCheckResult.Healthy(
                data: new Dictionary<string, object>
                {
                    ["emailDeliveryCutoverUtc"] = cutover?.ToString("O", CultureInfo.InvariantCulture)!,
                    ["cutoverValid"] = valid,
                    ["mode"] = mode,
                }
            )
        );
    }
}
