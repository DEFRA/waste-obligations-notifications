namespace Defra.WasteObligations.Consumer.Administration;

public sealed record CommandDlqMessageRedriveResult(string MessageId, string Outcome);
