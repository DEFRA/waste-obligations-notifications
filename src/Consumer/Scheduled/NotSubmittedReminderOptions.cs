using System.ComponentModel.DataAnnotations;

namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed record NotSubmittedReminderOptions
{
    public const string SectionName = "NotSubmittedReminder";

    public bool Enabled { get; init; }

    [Range(1, 12)]
    public int Month { get; init; } = 12;

    [Range(1, 31)]
    public int Day { get; init; } = 1;

    [Range(0, 23)]
    public int Hour { get; init; } = 10;

    [Range(0, 59)]
    public int Minute { get; init; }

    [Required]
    public string TimeZoneId { get; init; } = "Europe/London";

    [Range(typeof(TimeSpan), "00:01:00", "30.00:00:00")]
    public TimeSpan SendWindow { get; init; } = TimeSpan.FromDays(7);

    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromMinutes(30);

    [Range(1, 100)]
    public int PageSize { get; init; } = 100;

    [Range(2023, 2050)]
    public int? ObligationYearOverride { get; init; }

    public bool HasValidSchedule =>
        Month is >= 1 and <= 12
        && Day is >= 1 and <= 31
        && Hour is >= 0 and <= 23
        && Minute is >= 0 and <= 59
        && SendWindow >= TimeSpan.FromMinutes(1)
        && ScanInterval >= TimeSpan.FromMinutes(1)
        && PageSize is >= 1 and <= 100
        && !string.IsNullOrWhiteSpace(TimeZoneId);
}
