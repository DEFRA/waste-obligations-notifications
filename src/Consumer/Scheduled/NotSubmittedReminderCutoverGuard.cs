using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed class NotSubmittedReminderCutoverGuard(
    IOptions<NotSubmittedReminderOptions> reminderOptions,
    IOptions<NotificationCommandDeliveryOptions> deliveryOptions,
    NotSubmittedReminderOccurrence occurrence,
    TimeProvider timeProvider
)
{
    public const string DisabledReason = "not-submitted-reminder-disabled";
    public const string OutsideSendWindowReason = "not-submitted-reminder-outside-send-window";
    public const string CutoverMisconfiguredReason = "not-submitted-reminder-cutover-misconfigured";

    public string? GetSkipReason(int obligationYear)
    {
        var settings = reminderOptions.Value;
        if (!settings.Enabled)
            return DisabledReason;

        var occurrenceUtc = occurrence.GetOccurrenceUtc(obligationYear);
        var now = timeProvider.GetUtcNow();
        if (now < occurrenceUtc || now >= occurrenceUtc + settings.SendWindow)
            return OutsideSendWindowReason;

        if (!deliveryOptions.Value.TryReadCutover(out var cutover) || cutover is null || cutover.Value > occurrenceUtc)
            return CutoverMisconfiguredReason;

        return null;
    }
}
