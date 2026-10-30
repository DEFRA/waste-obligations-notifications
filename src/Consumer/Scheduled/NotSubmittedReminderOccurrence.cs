using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed class NotSubmittedReminderOccurrence(IOptions<NotSubmittedReminderOptions> options)
{
    public DateTimeOffset GetOccurrenceUtc(int obligationYear)
    {
        var settings = options.Value;
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var local = new DateTime(
            obligationYear,
            settings.Month,
            settings.Day,
            settings.Hour,
            settings.Minute,
            0,
            DateTimeKind.Unspecified
        );
        var offset = timeZone.GetUtcOffset(local);
        var localWithOffset = new DateTimeOffset(local, offset);

        return localWithOffset.ToUniversalTime();
    }
}
