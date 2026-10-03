namespace Defra.WasteObligations.Consumer.Utils.Metrics;

public static class MetricNames
{
    public const string NotificationCommandReceived = nameof(NotificationCommandReceived);
    public const string NotificationCommandOutcome = nameof(NotificationCommandOutcome);
    public const string NotificationCommandLeaseClaim = nameof(NotificationCommandLeaseClaim);
    public const string NotificationCommandLeaseClaimDuration = nameof(NotificationCommandLeaseClaimDuration);
    public const string NotificationCommandNotifySendAccepted = nameof(NotificationCommandNotifySendAccepted);
    public const string NotificationCommandNotifySendFailure = nameof(NotificationCommandNotifySendFailure);
    public const string NotificationCommandNotifySendDuration = nameof(NotificationCommandNotifySendDuration);
    public const string NotificationCommandDuplicateSuppressed = nameof(NotificationCommandDuplicateSuppressed);
}
