namespace Defra.WasteObligations.Consumer.Utils.Metrics;

public sealed class EmfDiagnosticLogger(ILogger logger) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && logger.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        if (IsEnabled(logLevel))
            logger.Log(logLevel, "Notification command EMF SDK reported a warning or error.");
    }
}
