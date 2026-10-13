namespace Defra.WasteObligations.Consumer.Administration;

public sealed record CommandDlqAction(string Method, string Endpoint, string Description);
