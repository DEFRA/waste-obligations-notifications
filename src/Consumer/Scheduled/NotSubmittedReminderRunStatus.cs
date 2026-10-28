namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed record NotSubmittedReminderRunStatus(
    string Campaign,
    int ObligationYear,
    DateTimeOffset SendWindowStartUtc,
    DateTimeOffset SendWindowEndUtc,
    string Status,
    int ScanCount,
    DateTime? LastScanStartedAtUtc,
    DateTime? LastScanFinishedAtUtc,
    DateTime? CompletedAtUtc,
    int OrganisationsProcessed,
    int CommandsPublished,
    int OrganisationsSkipped,
    int RetryableFailures
);
