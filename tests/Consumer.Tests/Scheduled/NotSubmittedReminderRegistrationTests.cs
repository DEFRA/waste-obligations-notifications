using Defra.WasteObligations.Consumer.Notifications;
using Defra.WasteObligations.Consumer.Scheduled;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Defra.WasteObligations.Consumer.Tests.Scheduled;

public sealed class NotSubmittedReminderRegistrationTests
{
    private const string SecondSection = "NotSubmittedReminder2";
    private const string ReportUrlKey = $"{SecondSection}:Personalisation:reportPackagingDataAccountUrl";

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void WhenCheckedInSettingsAreBound_ShouldKeepFirstReminderAndScheduleSecondInJanuary(string settingsFile)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryRoot(), "src", "Consumer", settingsFile))
            .Build();
        using var provider = CreateProvider(configuration);
        var monitor = provider.GetRequiredService<IOptionsMonitor<NotSubmittedReminderOptions>>();

        var first = monitor.Get("NotSubmittedReminder");
        var second = monitor.Get(SecondSection);

        Assert.Equal((false, 12, 1, 10), (first.Enabled, first.Month, first.Day, first.Hour));
        Assert.Empty(first.Personalisation);
        Assert.Equal((false, 1, 1, 10), (second.Enabled, second.Month, second.Day, second.Hour));
        Assert.Equal(
            "https://rwd-dev9.azure.defra.cloud/report-data",
            second.Personalisation["reportPackagingDataAccountUrl"]
        );

        var definitions = configuration
            .GetSection(NotificationDefinitionsOptions.SectionName)
            .Get<NotificationDefinitionsOptions>()!;
        Assert.Equal(
            "458cd86f-861b-46ad-b67f-560a3c78ee94",
            definitions.GetTemplate(
                NotificationDefinitionsOptions.DeclarationNotSubmittedReminder2,
                "EnglishTemplateId"
            )
        );
        Assert.Equal(
            "5f9e4390-41d6-418a-99bd-d0035ba39bc2",
            definitions.GetTemplate(NotificationDefinitionsOptions.DeclarationNotSubmittedReminder2, "WelshTemplateId")
        );
        Assert.Subset(
            configuration
                .GetSection("NotificationCommandDelivery:DiagnosticNotificationTypes")
                .Get<string[]>()!
                .ToHashSet(),
            new HashSet<string> { "declaration-not-submitted-reminder-2-en", "declaration-not-submitted-reminder-2-cy" }
        );
    }

    [Fact]
    public void WhenEnvironmentOverridesPersonalisation_ShouldUseOverriddenValue()
    {
        using var provider = CreateProvider(
            CreateConfiguration(
                new()
                {
                    [$"{SecondSection}:Enabled"] = "true",
                    [ReportUrlKey] = "https://rwd-dev9.azure.defra.cloud/report-data",
                },
                new() { [ReportUrlKey] = "https://report-packaging-data.example.test/report-data" }
            )
        );

        var options = provider.GetRequiredService<IOptionsMonitor<NotSubmittedReminderOptions>>().Get(SecondSection);

        Assert.Equal(
            "https://report-packaging-data.example.test/report-data",
            options.Personalisation["reportPackagingDataAccountUrl"]
        );
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("reportPackagingDataAccountUrl", " ")]
    [InlineData("firstName", "Replaced")]
    [InlineData("CertYear", "2030")]
    public void WhenSecondReminderEnabledWithInvalidPersonalisation_ShouldRejectOptions(string? key, string? value)
    {
        var values = new Dictionary<string, string?> { [$"{SecondSection}:Enabled"] = "true" };
        if (key is not null and not "reportPackagingDataAccountUrl")
            values[ReportUrlKey] = "https://example.test/report-data";
        if (key is not null)
            values[$"{SecondSection}:Personalisation:{key}"] = value;
        using var provider = CreateProvider(CreateConfiguration(values));

        var monitor = provider.GetRequiredService<IOptionsMonitor<NotSubmittedReminderOptions>>();

        var exception = Assert.Throws<OptionsValidationException>(() => monitor.Get(SecondSection));
        Assert.Contains(exception.Failures, failure => failure.StartsWith(SecondSection, StringComparison.Ordinal));
        Assert.All(exception.Failures, failure => Assert.DoesNotContain("example.test", failure));
    }

    [Fact]
    public void WhenSecondReminderDisabled_ShouldNotRequirePersonalisation()
    {
        using var provider = CreateProvider(CreateConfiguration([]));

        var options = provider.GetRequiredService<IOptionsMonitor<NotSubmittedReminderOptions>>().Get(SecondSection);

        Assert.False(options.Enabled);
    }

    [Fact]
    public void AddNotSubmittedReminderProducer_ShouldRegisterOneWorkerPerCampaign()
    {
        var services = new ServiceCollection();

        services.AddNotSubmittedReminderProducer(CreateConfiguration([]));

        Assert.Equal(
            NotSubmittedReminderCampaign.All.Count,
            services.Count(service =>
                service.ServiceType == typeof(IHostedService)
                && service.ImplementationFactory?.Method.ReturnType == typeof(NotSubmittedReminderWorker)
            )
        );
        Assert.All(
            NotSubmittedReminderCampaign.All,
            campaign =>
                Assert.Contains(
                    services,
                    service =>
                        service.ServiceType == typeof(INotSubmittedReminderScanService)
                        && Equals(service.ServiceKey, campaign.Id)
                )
        );
    }

    private static ServiceProvider CreateProvider(IConfiguration configuration) =>
        new ServiceCollection().AddNotSubmittedReminderProducer(configuration).BuildServiceProvider();

    private static IConfiguration CreateConfiguration(
        Dictionary<string, string?> values,
        Dictionary<string, string?>? overrides = null
    ) => new ConfigurationBuilder().AddInMemoryCollection(values).AddInMemoryCollection(overrides ?? []).Build();

    private static string RepositoryRoot()
    {
        for (
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
            if (File.Exists(Path.Combine(directory.FullName, "waste-obligations-notifications.slnx")))
                return directory.FullName;

        throw new InvalidOperationException("Repository root was not found for reminder configuration.");
    }
}
