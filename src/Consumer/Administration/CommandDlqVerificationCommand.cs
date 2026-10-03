namespace Defra.WasteObligations.Consumer.Administration;

public sealed record CommandDlqVerificationCommand(string IdempotencyKey, string MessageId);
