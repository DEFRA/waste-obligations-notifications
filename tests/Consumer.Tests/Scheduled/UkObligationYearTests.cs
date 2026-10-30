using Defra.WasteObligations.Consumer.Scheduled;
using Xunit;

namespace Defra.WasteObligations.Consumer.Tests.Scheduled;

public sealed class UkObligationYearTests
{
    [Theory]
    [InlineData("2026-01-15T12:00:00Z", 2025)]
    [InlineData("2026-06-15T12:00:00Z", 2026)]
    public void FromUtcNow_ShouldUseUnitedKingdomCalendarYear(string utcNow, int expectedYear)
    {
        var time = new FakeTimeProvider(
            DateTimeOffset.Parse(utcNow, System.Globalization.CultureInfo.InvariantCulture)
        );

        Assert.Equal(expectedYear, UkObligationYear.FromUtcNow(time));
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
