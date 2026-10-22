namespace Defra.WasteObligations.Consumer.Utils.Metrics;

public static class MetricNames
{
    public const string AnalyticsEventReceived = nameof(AnalyticsEventReceived);
    public const string AnalyticsEventOutcome = nameof(AnalyticsEventOutcome);
    public const string AnalyticsEventProcessingDuration = nameof(AnalyticsEventProcessingDuration);
    public const string NotificationCommandPublished = nameof(NotificationCommandPublished);
    public const string NotificationProcessingFailure = nameof(NotificationProcessingFailure);
    public const string NotificationConsumerPoll = nameof(NotificationConsumerPoll);
    public const string NotificationDeliveryCutoverConfigured = nameof(NotificationDeliveryCutoverConfigured);
    public const string NotificationCommandReceived = nameof(NotificationCommandReceived);
    public const string NotificationCommandOutcome = nameof(NotificationCommandOutcome);
    public const string NotificationCommandLeaseClaim = nameof(NotificationCommandLeaseClaim);
    public const string NotificationCommandLeaseClaimDuration = nameof(NotificationCommandLeaseClaimDuration);
    public const string NotificationCommandNotifySendAccepted = nameof(NotificationCommandNotifySendAccepted);
    public const string NotificationCommandNotifySendFailure = nameof(NotificationCommandNotifySendFailure);
    public const string NotificationCommandNotifySendDuration = nameof(NotificationCommandNotifySendDuration);
    public const string NotificationCommandNotifyAcceptanceLatency = nameof(NotificationCommandNotifyAcceptanceLatency);
    public const string NotificationCommandOutcomeLatency = nameof(NotificationCommandOutcomeLatency);
    public const string NotificationCommandDuplicateSuppressed = nameof(NotificationCommandDuplicateSuppressed);
}
