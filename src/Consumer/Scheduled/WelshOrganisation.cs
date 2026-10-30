namespace Defra.WasteObligations.Consumer.Scheduled;

public static class WelshOrganisation
{
    public static bool IsWelsh(string? businessCountry) =>
        string.Equals(businessCountry?.Trim(), "GB-WLS", StringComparison.Ordinal);
}
