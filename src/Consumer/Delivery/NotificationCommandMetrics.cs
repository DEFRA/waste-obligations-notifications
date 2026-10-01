using System.Diagnostics;
using System.Diagnostics.Metrics;
using Amazon.CloudWatch.EMF.Model;
using Defra.WasteObligations.Consumer.Utils.Metrics;

namespace Defra.WasteObligations.Consumer.Delivery;

public sealed class NotificationCommandMetrics : INotificationCommandMetrics
{
    private readonly Counter<long> _received;
    private readonly Counter<long> _outcomes;
    private readonly Counter<long> _claims;
    private readonly Histogram<double> _claimDuration;
    private readonly Counter<long> _sendAccepted;
    private readonly Counter<long> _sendFailure;
    private readonly Histogram<double> _sendDuration;
    private readonly Counter<long> _duplicates;

    public NotificationCommandMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(Metrics.MeterName);
        _received = meter.CreateCounter<long>(
            MetricNames.NotificationCommandReceived,
            nameof(Unit.COUNT),
            "Count of validated notification commands received"
        );
        _outcomes = meter.CreateCounter<long>(
            MetricNames.NotificationCommandOutcome,
            nameof(Unit.COUNT),
            "Count of terminal notification command outcomes"
        );
        _claims = meter.CreateCounter<long>(
            MetricNames.NotificationCommandLeaseClaim,
            nameof(Unit.COUNT),
            "Count of notification command claim outcomes"
        );
        _claimDuration = meter.CreateHistogram<double>(
            MetricNames.NotificationCommandLeaseClaimDuration,
            nameof(Unit.MILLISECONDS),
            "Elapsed time spent claiming a notification command"
        );
        _sendAccepted = meter.CreateCounter<long>(
            MetricNames.NotificationCommandNotifySendAccepted,
            nameof(Unit.COUNT),
            "Count of Notify attempts with confirmed acceptance"
        );
        _sendFailure = meter.CreateCounter<long>(
            MetricNames.NotificationCommandNotifySendFailure,
            nameof(Unit.COUNT),
            "Count of Notify attempts without confirmed acceptance"
        );
        _sendDuration = meter.CreateHistogram<double>(
            MetricNames.NotificationCommandNotifySendDuration,
            nameof(Unit.MILLISECONDS),
            "Elapsed time spent sending a notification through Notify"
        );
        _duplicates = meter.CreateCounter<long>(
            MetricNames.NotificationCommandDuplicateSuppressed,
            nameof(Unit.COUNT),
            "Count of terminal duplicate notification commands suppressed"
        );
    }

    public void RecordReceived(string notificationType) => _received.Add(1, BuildTags(notificationType));

    public void RecordOutcome(string notificationType, string outcome)
    {
        var tags = BuildTags(notificationType);
        tags.Add(MetricTags.Outcome, outcome);
        _outcomes.Add(1, tags);
    }

    public void RecordClaim(string notificationType, DeliveryClaimResult result, double elapsedMilliseconds) =>
        RecordClaimOutcome(
            notificationType,
            result switch
            {
                DeliveryClaimResult.Claimed => "claimed",
                DeliveryClaimResult.TerminalDuplicate => "terminal-duplicate",
                DeliveryClaimResult.Conflict => "conflict",
                DeliveryClaimResult.ActiveClaim => "active-claim",
                _ => "unavailable",
            },
            elapsedMilliseconds
        );

    public void RecordClaimFailure(string notificationType, double elapsedMilliseconds) =>
        RecordClaimOutcome(notificationType, "failure", elapsedMilliseconds);

    public void RecordSendAccepted(string notificationType) => _sendAccepted.Add(1, BuildTags(notificationType));

    public void RecordSendFailure(string notificationType) => _sendFailure.Add(1, BuildTags(notificationType));

    public void RecordSendDuration(string notificationType, double elapsedMilliseconds) =>
        _sendDuration.Record(elapsedMilliseconds, BuildTags(notificationType));

    public void RecordDuplicate(string notificationType) => _duplicates.Add(1, BuildTags(notificationType));

    private void RecordClaimOutcome(string notificationType, string outcome, double elapsedMilliseconds)
    {
        var tags = BuildTags(notificationType);
        tags.Add(MetricTags.Outcome, outcome);
        _claims.Add(1, tags);
        _claimDuration.Record(elapsedMilliseconds, tags);
    }

    private static TagList BuildTags(string notificationType) =>
        new() { { MetricTags.Service, Metrics.ServiceName }, { MetricTags.NotificationType, notificationType } };
}
