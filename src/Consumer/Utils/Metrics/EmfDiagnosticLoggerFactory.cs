namespace Defra.WasteObligations.Consumer.Utils.Metrics;

public sealed class EmfDiagnosticLoggerFactory(ILogger<EmfDiagnosticLoggerFactory> logger) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new EmfDiagnosticLogger(logger);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }
}
