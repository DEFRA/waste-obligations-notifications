using Defra.WasteObligations.Consumer.Scheduled;
using Xunit;

namespace Defra.WasteObligations.Consumer.Tests.Scheduled;

public sealed class RegulatorEmailResolverTests
{
    [Theory]
    [InlineData("GB-ENG", "packagingproducers@environment-agency.gov.uk", false)]
    [InlineData("GB-SCT", "producer.responsibility@sepa.org.uk", false)]
    [InlineData("GB-WLS", "packaging@naturalresourceswales.gov.uk", false)]
    [InlineData("GB-NIR", "packaging@daera-ni.gov.uk", false)]
    public void Resolve_KnownCountry_ShouldReturnAgencyEmail(string country, string expectedEmail, bool expectedDefault)
    {
        var (normalised, email, usedDefault) = RegulatorEmailResolver.Resolve(country);

        Assert.Equal(country, normalised);
        Assert.Equal(expectedEmail, email);
        Assert.Equal(expectedDefault, usedDefault);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void Resolve_UnknownCountry_ShouldDefaultToEngland(string? country)
    {
        var (normalised, email, usedDefault) = RegulatorEmailResolver.Resolve(country);

        Assert.Equal(RegulatorEmailResolver.DefaultBusinessCountry, normalised);
        Assert.Equal("packagingproducers@environment-agency.gov.uk", email);
        Assert.True(usedDefault);
    }
}
