namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed record NotSubmittedReminderScanResult(
    int OrganisationsProcessed,
    int CommandsPublished,
    int OrganisationsSkipped,
    string? SkipReason
);
