namespace Defra.WasteObligations.Consumer.Administration;

public sealed record CommandDlqRedriveTaskStarted(string TaskHandle);
