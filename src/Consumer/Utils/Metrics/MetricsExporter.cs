using System.Diagnostics.Metrics;
using Amazon.CloudWatch.EMF.Logger;
using Amazon.CloudWatch.EMF.Model;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Logging.Abstractions;

namespace Defra.WasteObligations.Consumer.Utils.Metrics;

public static class MetricsExporter
{
    private static readonly MeterListener s_listener = new();
    private static ILogger Logger { get; set; } = NullLogger.Instance;
    private static string s_namespace = string.Empty;

    public static void Init(ILoggerFactory loggerFactory, string awsNamespace)
    {
        Logger = loggerFactory.CreateLogger(nameof(MetricsExporter));
        s_namespace = awsNamespace;
        s_listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == Metrics.MeterName && IsCommandInstrument(instrument.Name))
                listener.EnableMeasurementEvents(instrument);
        };
        s_listener.SetMeasurementEventCallback<int>(OnMeasurementRecorded);
        s_listener.SetMeasurementEventCallback<long>(OnMeasurementRecorded);
        s_listener.SetMeasurementEventCallback<double>(OnMeasurementRecorded);
        s_listener.Start();
    }

    private static void OnMeasurementRecorded<T>(
        Instrument instrument,
        T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state
    )
    {
        try
        {
            var unit = Enum.Parse<Unit>(instrument.Unit!);
            using var metricsLogger = new MetricsLogger(NullLoggerFactory.Instance);
            metricsLogger.SetNamespace(s_namespace);
            var dimensions = new DimensionSet(MetricTags.Service, Metrics.ServiceName);
            foreach (var tag in tags)
            {
                var value = tag.Value?.ToString();
                if (IsCommandDimension(tag.Key, value))
                    dimensions.AddDimension(tag.Key, value);
            }
            metricsLogger.SetDimensions(dimensions);
            metricsLogger.PutMetric(instrument.Name, Convert.ToDouble(measurement), unit);
            // SDK 2.2.0 flushes on Dispose; an explicit Flush would emit a second empty document.
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private static void ReportFailure(Exception exception)
    {
        if (Logger.IsEnabled(LogLevel.Error))
            Logger.LogError("Notification command EMF export failed ({ExceptionType}).", exception.GetType().Name);
    }

    private static bool IsCommandInstrument(string name) =>
        name
            is MetricNames.NotificationCommandReceived
                or MetricNames.NotificationCommandOutcome
                or MetricNames.NotificationCommandLeaseClaim
                or MetricNames.NotificationCommandLeaseClaimDuration
                or MetricNames.NotificationCommandNotifySendAccepted
                or MetricNames.NotificationCommandNotifySendFailure
                or MetricNames.NotificationCommandNotifySendDuration
                or MetricNames.NotificationCommandOutcomeLatency
                or MetricNames.NotificationCommandDuplicateSuppressed
                or MetricNames.AnalyticsEventReceived
                or MetricNames.AnalyticsEventOutcome
                or MetricNames.AnalyticsEventProcessingDuration
                or MetricNames.NotificationCommandPublished
                or MetricNames.NotificationProcessingFailure
                or MetricNames.NotificationConsumerPoll
                or MetricNames.NotificationDeliveryCutoverConfigured;

    private static bool IsCommandDimension(string name, string? value) =>
        name == MetricTags.NotificationType && NotificationCommandDeliveryOptions.IsDiagnosticLabel(value)
        || name == MetricTags.Consumer
            && value is NotificationJourneyValues.Consumer.Analytics or NotificationJourneyValues.Consumer.Delivery
        || name == MetricTags.Source && value == NotificationJourneyValues.Source.Analytics
        || name == MetricTags.Stage
            && value
                is NotificationJourneyValues.Stage.Receive
                    or NotificationJourneyValues.Stage.Parse
                    or NotificationJourneyValues.Stage.ValidateEvent
                    or NotificationJourneyValues.Stage.ReadDeclaration
                    or NotificationJourneyValues.Stage.PublishCommand
                    or NotificationJourneyValues.Stage.Acknowledge
                    or NotificationJourneyValues.Stage.Suppression
                    or NotificationJourneyValues.Stage.Claim
                    or NotificationJourneyValues.Stage.Send
                    or NotificationJourneyValues.Stage.RecordAcceptance
                    or NotificationJourneyValues.Stage.ProcessingBudget
        || name == MetricTags.Outcome
            && value
                is NotificationDeliveryClassifications.DeliveryAccepted
                    or NotificationDeliveryClassifications.DeliverySuppressed
                    or NotificationDeliveryClassifications.DeliveryAbandoned
                    or "terminal-duplicate"
                    or "claimed"
                    or "conflict"
                    or NotificationDeliveryClassifications.ActiveClaim
                    or "unavailable"
                    or "failure"
                    or NotificationJourneyValues.Outcome.Ignored
                    or NotificationJourneyValues.Outcome.NoNotifications
                    or NotificationJourneyValues.Outcome.CommandsPublished;
}
