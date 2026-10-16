namespace Defra.WasteObligations.Consumer.Tests.Administration;

// Program replaces and disposes the process-global Serilog logger for each host.
[CollectionDefinition(nameof(ProductionLoggingCollection), DisableParallelization = true)]
public sealed class ProductionLoggingCollection;
