using System.Diagnostics.Metrics;

namespace Defra.WasteObligations.Consumer.Delivery;

public sealed class NotificationCommandMetrics
{
    private const string MeterName = "Defra.WasteObligations.Notifications.Commands";
    private readonly Counter<long> _received;
    private readonly Counter<long> _outcomes;

    public NotificationCommandMetrics()
    {
        var meter = new Meter(MeterName);
        _received = meter.CreateCounter<long>("notification_command_received");
        _outcomes = meter.CreateCounter<long>("notification_command_outcome");
    }

    public void RecordReceived(string notificationType) =>
        _received.Add(1, new KeyValuePair<string, object?>("notification.type", notificationType));

    public void RecordOutcome(string notificationType, string outcome) =>
        _outcomes.Add(
            1,
            new KeyValuePair<string, object?>("notification.type", notificationType),
            new KeyValuePair<string, object?>("outcome", outcome)
        );
}
