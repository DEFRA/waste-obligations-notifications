namespace Defra.WasteObligations.Consumer.Scheduled;

public static class UkObligationYear
{
    private static readonly TimeZoneInfo UnitedKingdom = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public static int FromUtcNow(TimeProvider timeProvider)
    {
        var localNow = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), UnitedKingdom);

        return localNow.Month is 1 ? localNow.Year - 1 : localNow.Year;
    }
}
