using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
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
    [InlineData(false, "https://organisations.test/", true)]
    [InlineData(true, "https://organisations.test/", false)]
    [InlineData(false, "http://organisations.test/", false)]
    [InlineData(false, "http://localhost:8082/", true)]
    public void WhenWasteOrganisationsAccessIsBound_ShouldRequireNamedCredentialsAndSecureAddress(
        bool legacyNames,
        string address,
        bool valid
    )
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["WasteOrganisations:BaseAddress"] = address,
                    [$"WasteOrganisations:{(legacyNames ? "ClientId" : "Username")}"] = "user",
                    [$"WasteOrganisations:{(legacyNames ? "ClientSecret" : "Password")}"] = "secret",
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<AnalyticsEventConsumerOptions>>(
            Options.Create(
                new AnalyticsEventConsumerOptions { QueueUrl = "http://localhost/analytics", ProcessingEnabled = true }
            )
        );
        services.AddWasteOrganisationsClient(configuration);
        using var provider = services.BuildServiceProvider();

        if (!valid)
            Assert.Throws<OptionsValidationException>(() =>
                provider.GetRequiredService<IOptions<WasteOrganisationsOptions>>().Value
            );
        else
        {
            var options = provider.GetRequiredService<IOptions<WasteOrganisationsOptions>>().Value;
            Assert.Equal("user", options.Username);
            Assert.Equal("secret", options.Password);
            Assert.True(options.HasReadAccess);
        }
    }

    [Theory]
    [InlineData("https://user:secret@declarations.test/")]
    [InlineData("http://declarations.test/")]
    public void WhenWasteObligationsAddressIsInvalid_ShouldRejectEnabledAccess(string address)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["WasteObligations:BaseAddress"] = address,
                    ["WasteObligations:Username"] = "reader",
                    ["WasteObligations:Password"] = "secret",
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<AnalyticsEventConsumerOptions>>(
            Options.Create(
                new AnalyticsEventConsumerOptions { QueueUrl = "http://localhost/analytics", ProcessingEnabled = true }
            )
        );
        services.AddWasteObligationsClient(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<WasteObligationsOptions>>().Value
        );
    }

    [Theory]
    [InlineData("http://accounts.example.test", "http://accounts.example.test/token", false)]
    [InlineData("https://accounts.example.test", "https://accounts.example.test/token", true)]
    [InlineData("http://localhost:8083", "http://localhost:8083/token", true)]
    [InlineData("https://accounts.example.test", "http://accounts.example.test/token", false)]
    [InlineData("http://accounts.example.test", "https://accounts.example.test/token", false)]
    public void WhenAccountAddressCarriesCredentials_ShouldRequireHttpsExceptLoopback(
        string address,
        string tokenEndpoint,
        bool valid
    )
    {
        var options = new AccountBackendOptions
        {
            BaseAddress = address,
            TokenEndpoint = tokenEndpoint,
            ClientId = "client",
            ClientSecret = "secret",
        };

        Assert.Equal(valid, options.HasReadAccess);
    }
}
