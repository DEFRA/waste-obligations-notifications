using Defra.WasteObligations.Consumer.Services.Http;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
using Defra.WasteObligations.Consumer.Services.WasteOrganisations;

namespace Defra.WasteObligations.Consumer.Tests.Services;

public sealed class BasicAuthenticationOptionsTests
{
    [Theory]
    [InlineData("user", "secret:tail", true)]
    [InlineData("user:invalid", "secret", false)]
    [InlineData("user", "set-automatically-when-deployed", false)]
    [InlineData(" ", "secret", false)]
    public void WhenBasicCredentialsAreValidated_ShouldShareCredentialRules(
        string username,
        string password,
        bool valid
    )
    {
        BasicAuthenticationOptions[] options =
        [
            new WasteObligationsOptions { Username = username, Password = password },
            new WasteOrganisationsOptions { Username = username, Password = password },
        ];

        Assert.All(options, settings => Assert.Equal(valid, settings.HasValidCredentials));
    }
}
