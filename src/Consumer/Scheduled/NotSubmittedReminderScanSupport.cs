using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed record NotSubmittedReminderScanSupport(
    IOptions<NotSubmittedReminderOptions> Options,
    NotSubmittedReminderPresentation Presentation,
    NotSubmittedReminderOccurrence Occurrence,
    NotSubmittedReminderCutoverGuard CutoverGuard,
    TimeProvider TimeProvider
);
