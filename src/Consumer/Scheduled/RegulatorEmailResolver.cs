namespace Defra.WasteObligations.Consumer.Scheduled;

public static class RegulatorEmailResolver
{
    public const string DefaultBusinessCountry = "GB-ENG";

    private static readonly Dictionary<string, string> Emails = new(StringComparer.Ordinal)
    {
        ["GB-ENG"] = "packagingproducers@environment-agency.gov.uk",
        ["GB-SCT"] = "producer.responsibility@sepa.org.uk",
        ["GB-WLS"] = "packaging@naturalresourceswales.gov.uk",
        ["GB-NIR"] = "packaging@daera-ni.gov.uk",
    };

    public static (string NormalisedCountry, string RegulatorEmail, bool UsedDefaultCountry) Resolve(
        string? businessCountry
    )
    {
        var trimmed = businessCountry?.Trim();
        if (trimmed is not null && Emails.TryGetValue(trimmed, out var known))
            return (trimmed, known, false);

        return (DefaultBusinessCountry, Emails[DefaultBusinessCountry], true);
    }
}
