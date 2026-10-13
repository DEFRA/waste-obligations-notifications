using System.Collections.Concurrent;
using System.Globalization;
using Elastic.CommonSchema.Serilog;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace Defra.WasteObligations.Consumer.Tests.Administration;

public sealed class AdminRequestLogCapture : ILogEventSink, IDisposable
{
    private const int MaximumEvents = 512;
    private static readonly ConcurrentDictionary<string, AdminRequestLogCapture> s_captures = new();
    private int _eventCount;

    public AdminRequestLogCapture()
    {
        Id = Guid.NewGuid().ToString("N");
        if (!s_captures.TryAdd(Id, this))
            throw new InvalidOperationException("Request log capture registration failed.");
    }

    public string Id { get; }
    public ConcurrentQueue<LogEvent> Events { get; } = new();
    public ConcurrentQueue<string> EcsOutput { get; } = new();
    public bool Overflowed => Volatile.Read(ref _eventCount) > MaximumEvents;

    public void Emit(LogEvent logEvent)
    {
        if (Interlocked.Increment(ref _eventCount) > MaximumEvents)
            return;
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        new EcsTextFormatter().Format(logEvent, writer);
        EcsOutput.Enqueue(writer.ToString());
        Events.Enqueue(logEvent);
    }

    public static AdminRequestLogCapture Find(string captureId) =>
        s_captures.TryGetValue(captureId, out var capture)
            ? capture
            : throw new InvalidOperationException("Request log capture is not registered.");

    public void Dispose()
    {
        s_captures.TryRemove(Id, out _);
        GC.SuppressFinalize(this);
    }
}

public static class AdminRequestLogCaptureSinkExtensions
{
    public static LoggerConfiguration CaptureAdminRequests(
        this LoggerSinkConfiguration configuration,
        string captureId
    ) => configuration.Sink(AdminRequestLogCapture.Find(captureId));
}
