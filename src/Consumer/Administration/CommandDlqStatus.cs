namespace Defra.WasteObligations.Consumer.Administration;

public sealed record CommandDlqStatus(
    long ApproximateVisibleMessages,
    long ApproximateInFlightMessages,
    long ApproximateDelayedMessages,
    long ApproximateTotalMessages
);
