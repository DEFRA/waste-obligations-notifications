using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteOrganisations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Tests.Services;

public sealed class CancellationDependencyRegistrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenAccessIsMissing_ShouldRequireCredentialsOnlyForEnabledAnalytics(bool enabled)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<AnalyticsEventConsumerOptions>>(
            Options.Create(
                new AnalyticsEventConsumerOptions
                {
                    QueueUrl = "http://localhost/analytics",
                    ProcessingEnabled = enabled,
                }
            )
        );
        var configuration = new ConfigurationBuilder().Build();
        services.AddAccountBackendClient(configuration).AddWasteOrganisationsClient(configuration);
        using var provider = services.BuildServiceProvider();

        if (enabled)
        {
            Assert.Throws<OptionsValidationException>(() =>
                provider.GetRequiredService<IOptions<AccountBackendOptions>>().Value
            );
            Assert.Throws<OptionsValidationException>(() =>
                provider.GetRequiredService<IOptions<WasteOrganisationsOptions>>().Value
            );
        }
        else
        {
            Assert.NotNull(provider.GetRequiredService<IAccountBackendClient>());
            Assert.NotNull(provider.GetRequiredService<IWasteOrganisationsClient>());
        }

        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        Assert.Equal(2, registrations.Count);
        Assert.All(
            registrations,
            registration =>
            {
                Assert.Equal(TimeSpan.FromSeconds(10), registration.Timeout);
                Assert.Contains("extended", registration.Tags);
                Assert.DoesNotContain("ready", registration.Tags);
            }
        );
    }

    [Theory]
    [InlineData("http://accounts.example.test", false)]
    [InlineData("https://accounts.example.test", true)]
    [InlineData("http://localhost:8083", true)]
    public void WhenAccountAddressCarriesCredentials_ShouldRequireHttpsExceptLoopback(string address, bool valid)
    {
        var options = new AccountBackendOptions
        {
            BaseAddress = address,
            TokenEndpoint = address + "/token",
            ClientId = "client",
            ClientSecret = "secret",
        };

        Assert.Equal(valid, options.HasReadAccess);
    }
}
