using System.Globalization;
using Defra.WasteObligations.Consumer.Scheduled;
using Microsoft.Extensions.Options;
using Xunit;

namespace Defra.WasteObligations.Consumer.Tests.Scheduled;

public sealed class NotSubmittedReminderOccurrenceTests
{
    [Theory]
    [InlineData(12, 1, "2026-12-01T10:00:00Z")]
    [InlineData(6, 1, "2026-06-01T09:00:00Z")]
    [InlineData(1, 1, "2027-01-01T10:00:00Z")]
    [InlineData(1, 31, "2027-01-31T10:00:00Z")]
    public void GetOccurrenceUtc_ShouldPlaceJanuaryAfterTheObligationYear(int month, int day, string expected)
    {
        var occurrence = new NotSubmittedReminderOccurrence(
            Options.Create(
                new NotSubmittedReminderOptions
                {
                    Month = month,
                    Day = day,
                    Hour = 10,
                }
            )
        );

        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), occurrence.GetOccurrenceUtc(2026));
    }
}
