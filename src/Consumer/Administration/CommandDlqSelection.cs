namespace Defra.WasteObligations.Consumer.Administration;

public sealed record CommandDlqSelection(
    string ReceiveRequestAttemptId,
    string MessageId,
    string QueueBinding,
    DateTimeOffset ExpiresAtUtc,
    string ImmutableFieldsDigest,
    int VisibilityTimeoutSeconds
);
