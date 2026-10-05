using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Defra.WasteObligations.Consumer.Utils.Health;

public sealed class NotifyHealthCheck(INotifyEmailClient notifyClient) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await notifyClient.CheckHealth(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            return HealthCheckResult.Healthy("Connected to GOV.UK Notify.");
        }
        catch (Exception)
        {
            // Health results and framework logs must not expose dependency exceptions or template content.
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                description: "Failed to connect to GOV.UK Notify."
            );
        }
    }
}
