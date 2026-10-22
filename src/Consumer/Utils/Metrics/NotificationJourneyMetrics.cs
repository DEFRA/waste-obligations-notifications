using System.Diagnostics;
using System.Diagnostics.Metrics;
using Amazon.CloudWatch.EMF.Model;

namespace Defra.WasteObligations.Consumer.Utils.Metrics;

public sealed class NotificationJourneyMetrics : INotificationJourneyMetrics
{
    private readonly Counter<long> _analyticsReceived;
    private readonly Counter<long> _analyticsOutcome;
    private readonly Histogram<double> _analyticsDuration;
    private readonly Counter<long> _published;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _polls;
    private readonly Histogram<int> _cutover;

    public NotificationJourneyMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(Metrics.MeterName);
        _analyticsReceived = meter.CreateCounter<long>(
            MetricNames.AnalyticsEventReceived,
            nameof(Unit.COUNT),
            "Analytics message receipts, including retries and invalid messages"
        );
        _analyticsOutcome = meter.CreateCounter<long>(
            MetricNames.AnalyticsEventOutcome,
            nameof(Unit.COUNT),
            "Analytics outcomes with confirmed source acknowledgement"
        );
        _analyticsDuration = meter.CreateHistogram<double>(
            MetricNames.AnalyticsEventProcessingDuration,
            nameof(Unit.SECONDS),
            "Elapsed seconds handling an analytics message, including failed attempts"
        );
        _published = meter.CreateCounter<long>(
            MetricNames.NotificationCommandPublished,
            nameof(Unit.COUNT),
            "Command publications confirmed by the queue, including repeated publications"
        );
        _failures = meter.CreateCounter<long>(
            MetricNames.NotificationProcessingFailure,
            nameof(Unit.COUNT),
            "Processing failures by consumer and stage"
        );
        _polls = meter.CreateCounter<long>(
            MetricNames.NotificationConsumerPoll,
            nameof(Unit.COUNT),
            "Successful consumer receive operations, including empty receives"
        );
        _cutover = meter.CreateHistogram<int>(
            MetricNames.NotificationDeliveryCutoverConfigured,
            nameof(Unit.NONE),
            "Delivery policy observed on a successful poll: zero for unset cutover, one for configured cutover"
        );
    }

    public void RecordAnalyticsReceived() => _analyticsReceived.Add(1, ServiceTags());

    public void RecordAnalyticsOutcome(string outcome)
    {
        var tags = ServiceTags();
        tags.Add(MetricTags.Outcome, outcome);

        _analyticsOutcome.Add(1, tags);
    }

    public void RecordAnalyticsDuration(double elapsedSeconds) =>
        _analyticsDuration.Record(elapsedSeconds, ServiceTags());

    public void RecordCommandPublished(string source)
    {
        var tags = ServiceTags();
        tags.Add(MetricTags.Source, source);

        _published.Add(1, tags);
    }

    public void RecordFailure(string consumer, string stage)
    {
        var tags = ServiceTags();
        tags.Add(MetricTags.Consumer, consumer);
        tags.Add(MetricTags.Stage, stage);

        _failures.Add(1, tags);
    }

    public void RecordPoll(string consumer)
    {
        var tags = ServiceTags();
        tags.Add(MetricTags.Consumer, consumer);

        _polls.Add(1, tags);
    }

    public void RecordCutover(bool configured) => _cutover.Record(configured ? 1 : 0, ServiceTags());

    private static TagList ServiceTags() => new() { { MetricTags.Service, Metrics.ServiceName } };
}
