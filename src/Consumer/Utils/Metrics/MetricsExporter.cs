using System.Diagnostics.Metrics;
using Amazon.CloudWatch.EMF.Environment;
using Amazon.CloudWatch.EMF.Logger;
using Amazon.CloudWatch.EMF.Model;
using Amazon.CloudWatch.EMF.Sink;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Utils.Metrics;

public sealed class MetricsExporter(
    IMeterFactory meterFactory,
    IOptions<EmfOptions> options,
    IEmfEnvironmentFactory environmentFactory,
    EmfDiagnosticLoggerFactory sdkLoggerFactory,
    ILogger<MetricsExporter> logger
) : IHostedService, IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly object _gate = new();
    private IEnvironment? _environment;
    private ISink? _sink;
    private bool _observing;
    private int _shutdownStarted;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
            return Task.CompletedTask;

        try
        {
            _environment = environmentFactory.Create(cancellationToken);
            _sink = _environment.Sink;
            var meter = meterFactory.Create(Metrics.MeterName);
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<int>(OnMeasurementRecorded);
            _listener.SetMeasurementEventCallback<long>(OnMeasurementRecorded);
            _listener.SetMeasurementEventCallback<double>(OnMeasurementRecorded);
            _observing = true;
            _listener.Start();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            ReportFailure("startup", LogLevel.Error);
        }

        return Task.CompletedTask;
    }

    private void OnMeasurementRecorded<T>(
        Instrument instrument,
        T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state
    )
    {
        lock (_gate)
        {
            if (!_observing)
                return;

            try
            {
                // Preseed approved defaults so the SDK does not fetch/decorate metadata during delivery.
                var context = new MetricsContext
                {
                    DefaultDimensions = new DimensionSet(MetricTags.Service, Metrics.ServiceName),
                };
                using var metricsLogger = new MetricsLogger(_environment, context, sdkLoggerFactory);
                metricsLogger.SetNamespace(options.Value.EffectiveNamespace);
                var dimensions = new DimensionSet(MetricTags.Service, Metrics.ServiceName);
                foreach (var tag in tags)
                {
                    if (tag.Key is not (MetricTags.NotificationType or MetricTags.Outcome))
                        continue;
                    var value = tag.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                        dimensions.AddDimension(tag.Key, value);
                }
                metricsLogger.SetDimensions(dimensions);
                metricsLogger.PutMetric(
                    instrument.Name,
                    Convert.ToDouble(measurement),
                    Enum.Parse<Unit>(instrument.Unit!)
                );
            }
            catch
            {
                ReportFailure("measurement export", LogLevel.Error);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        lock (_gate)
            _observing = false;
        _listener.Dispose();
        if (_sink is null)
            return;

        try
        {
            var shutdown = _sink.Shutdown();
            // The SDK worker has no cancellation API; observe a fault even after our bounded wait ends.
            _ = shutdown.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
            await shutdown.WaitAsync(TimeSpan.FromSeconds(options.Value.ShutdownTimeoutSeconds), cancellationToken);
        }
        catch
        {
            ReportFailure("shutdown", LogLevel.Warning);
        }
    }

    private void ReportFailure(string operation, LogLevel level)
    {
        if (logger.IsEnabled(level))
            logger.Log(level, "Notification command EMF failure during {Operation}.", operation);
    }

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();
}
