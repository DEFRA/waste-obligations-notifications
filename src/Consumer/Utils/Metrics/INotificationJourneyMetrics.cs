namespace Defra.WasteObligations.Consumer.Utils.Metrics;

public interface INotificationJourneyMetrics
{
    void RecordAnalyticsReceived();
    void RecordAnalyticsOutcome(string outcome);
    void RecordAnalyticsDuration(double elapsedSeconds);
    void RecordCommandPublished(string source);
    void RecordFailure(string consumer, string stage, string? failureReason = null);
    void RecordPoll(string consumer);
    void RecordCutover(bool configured);
}
