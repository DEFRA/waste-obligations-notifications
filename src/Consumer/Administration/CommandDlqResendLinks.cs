namespace Defra.WasteObligations.Consumer.Administration;

public sealed record CommandDlqResendLinks(string SourceRecord, string ReplacementRecord);
