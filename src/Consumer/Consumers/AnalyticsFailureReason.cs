using Defra.WasteObligations.Consumer.Submission;
using Defra.WasteObligations.Consumer.Utils.Metrics;
using Polly.Timeout;

namespace Defra.WasteObligations.Consumer.Consumers;

public static class AnalyticsFailureReason
{
    public static bool IsKnown(string? reason) =>
        reason
            is "invalid-event-data"
                or "invalid-declaration-data"
                or "declaration-not-found"
                or "identity-mismatch"
                or "older-declaration-version"
                or "unknown-cancellation-reason"
                or "organisation-read-unavailable"
                or "organisation-not-found"
                or "account-read-unavailable"
                or "account-search-unavailable"
                or "processing-budget-exhausted"
                or "dependency-timeout"
                or "unconfirmed-publication"
                or "dependency-failure";

    public static string Classify(Exception exception, string stage) =>
        exception switch
        {
            SubmissionProcessingException failure when IsKnown(failure.Reason) => failure.Reason,
            TimeoutException or OperationCanceledException or TimeoutRejectedException => "dependency-timeout",
            _ when stage == NotificationJourneyValues.Stage.PublishCommand => "unconfirmed-publication",
            _ when stage is NotificationJourneyValues.Stage.Parse or NotificationJourneyValues.Stage.ValidateEvent =>
                "invalid-event-data",
            _ => "dependency-failure",
        };
}
