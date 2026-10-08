namespace Defra.WasteObligations.Consumer.Administration;

public sealed record CommandDlqRedriveTask(
    string Status,
    long? ApproximateMessagesMoved,
    long? ApproximateMessagesToMove,
    DateTimeOffset? StartedAtUtc
);
