using System.Net;
using Amazon.SQS;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Notifications;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Tests.Consumers;

public class ServiceCollectionExtensionsTests
{
    [Theory]
    [InlineData("Submission", "DirectEnglishTemplateId")]
    [InlineData("Cancellation", "ProducerRequestedCy")]
    public void WhenLegacyTemplateOverridesRemain_ShouldRejectStartupWithMigrationGuidance(
        string section,
        string template
    )
    {
        var configuration = CreateConfiguration();
        configuration[$"{section}:{template}"] = "11111111-1111-4111-8111-111111111111";
        using var provider = new ServiceCollection().AddAnalyticsEventConsumer(configuration).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IStartupValidator>().Validate()
        );

        Assert.Contains("migrate overrides to Notifications:Definitions:", exception.Message);
    }

    [Fact]
    public void WhenUnrelatedSettingsRemain_ShouldAcceptNotificationDefinitionOptions()
    {
        var configuration = CreateConfiguration();
        configuration["AnotherFeature:TemplateId"] = "not-a-notification-template";
        using var provider = new ServiceCollection().AddAnalyticsEventConsumer(configuration).BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();

        Assert.True(provider.GetRequiredService<IOptions<NotificationDefinitionsOptions>>().Value.HasValidTemplates);
    }

    [Fact]
    public void WhenAnotherDefinitionIsConfigured_ShouldBindAndResolveItsTemplates()
    {
        var configuration = CreateConfiguration();
        const string template = "11111111-1111-4111-8111-111111111111";
        configuration["Notifications:Definitions:AdditionalNotification:Templates:English"] = template;
        using var provider = new ServiceCollection().AddAnalyticsEventConsumer(configuration).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<NotificationDefinitionsOptions>>().Value;

        Assert.Equal(template, options.GetTemplate("AdditionalNotification", "English"));
        Assert.True(options.HasValidTemplates);
    }

    [Fact]
    public void WhenDefinitionAndTemplateOverrideUseUppercaseKeys_ShouldReplaceTheDefaultMapping()
    {
        var configuration = CreateConfiguration();
        const string template = "11111111-1111-4111-8111-111111111111";
        configuration["Notifications:Definitions:DECLARATIONSUBMITTED:Templates:DIRECTENGLISHTEMPLATEID"] = template;
        using var provider = new ServiceCollection().AddAnalyticsEventConsumer(configuration).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<NotificationDefinitionsOptions>>().Value;

        Assert.Equal(
            template,
            options.GetTemplate(NotificationDefinitionsOptions.DeclarationSubmitted, "DirectEnglishTemplateId")
        );
        Assert.Equal(3, options.Definitions.Count);
        Assert.Equal(4, options.Definitions[NotificationDefinitionsOptions.DeclarationSubmitted].Templates.Count);
    }

    [Fact]
    public void WhenRequiredTemplateIsRemoved_ShouldRejectTheDefinition()
    {
        var options = new NotificationDefinitionsOptions();
        options
            .Definitions[NotificationDefinitionsOptions.DeclarationSubmitted]
            .Templates.Remove("DirectEnglishTemplateId");

        Assert.False(options.HasValidTemplates);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void WhenNotificationDefinitionsOptionsUseDefaults_ShouldPreserveEachLegacyTemplateMapping(
        string? settingsFile
    )
    {
        var options = new NotificationDefinitionsOptions();
        if (settingsFile is not null)
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(RepositoryRoot(), "src", "Consumer", settingsFile))
                .Build();
            options = configuration
                .GetSection(NotificationDefinitionsOptions.SectionName)
                .Get<NotificationDefinitionsOptions>()!;
        }

        Assert.Equal(
            "5f64e3bd-d454-4a45-a9c6-9409bf940d7a",
            options.GetTemplate(NotificationDefinitionsOptions.DeclarationSubmitted, "DirectEnglishTemplateId")
        );
        Assert.Equal(
            "b3223b0b-a467-40c1-9150-f78b76d11fd8",
            options.GetTemplate(NotificationDefinitionsOptions.DeclarationSubmitted, "DirectWelshTemplateId")
        );
        Assert.Equal(
            "b103685d-de8a-4ea9-abc8-d244ca26841b",
            options.GetTemplate(NotificationDefinitionsOptions.DeclarationSubmitted, "SchemeEnglishTemplateId")
        );
        Assert.Equal(
            "95d8c2aa-a229-4ecb-9774-78ec06d0cbac",
            options.GetTemplate(NotificationDefinitionsOptions.DeclarationSubmitted, "SchemeWelshTemplateId")
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void WhenNotificationDefinitionsOptionsUseDefaults_ShouldPreserveAllEightMappings(string? settingsFile)
    {
        var options = new NotificationDefinitionsOptions();
        if (settingsFile is not null)
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(RepositoryRoot(), "src", "Consumer", settingsFile))
                .Build();
            options = configuration
                .GetSection(NotificationDefinitionsOptions.SectionName)
                .Get<NotificationDefinitionsOptions>()!;
        }

        Assert.Equal(
            "1502cfa2-9758-4410-b189-d1f95a7c774f",
            options.GetTemplate(NotificationDefinitionsOptions.DeclarationCancelled, "NotSignedByCorrectPersonEn")
        );
        Assert.Equal(
            "7cab1c5e-9edf-4139-b3fd-3db4bcf3041d",
            options.GetTemplate(NotificationDefinitionsOptions.DeclarationCancelled, "NotSignedByCorrectPersonCy")
        );
        Assert.Equal(
            "857808d4-d159-421e-92b6-bf9d59711a9e",
            options.GetTemplate(NotificationDefinitionsOptions.DeclarationCancelled, "RecyclingObligationsChangedEn")
        );
        Assert.Equal(
            "0a966633-b7ad-42dd-b286-af0cb2405ed2",
            options.GetTemplate(NotificationDefinitionsOptions.DeclarationCancelled, "RecyclingObligationsChangedCy")
        );
        Assert.Equal(
            "86345150-cb25-4b59-94c3-578bed7e82d4",
            options.GetTemplate(NotificationDefinitionsOptions.DeclarationCancelled, "CanMeetRecyclingObligationsEn")
        );
        Assert.Equal(
            "308e7224-0ab1-4f54-8e8d-1558c9c57a77",
            options.GetTemplate(NotificationDefinitionsOptions.DeclarationCancelled, "CanMeetRecyclingObligationsCy")
        );
        Assert.Equal(
            "3e03c93f-955c-4db3-936c-bfa3f7725a5f",
            options.GetTemplate(NotificationDefinitionsOptions.DeclarationCancelled, "ProducerRequestedEn")
        );
        Assert.Equal(
            "e419a544-e1b3-4ea5-b8eb-a074e63aea1a",
            options.GetTemplate(NotificationDefinitionsOptions.DeclarationCancelled, "ProducerRequestedCy")
        );
    }

    [Fact]
    public void WhenCancellationTemplateIsInvalid_ShouldRejectOptions()
    {
        var configuration = CreateConfiguration();
        configuration["Notifications:Definitions:DeclarationCancelled:Templates:ProducerRequestedCy"] = "not-a-uuid";
        using var provider = new ServiceCollection().AddAnalyticsEventConsumer(configuration).BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<NotificationDefinitionsOptions>>().Value
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void WhenCancellationBudgetUsesDefaults_ShouldCoverTheWholeAttemptWithHeadroom(string? settingsFile)
    {
        var options = new AnalyticsEventConsumerOptions { QueueUrl = "http://localhost/analytics" };
        if (settingsFile is not null)
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(RepositoryRoot(), "src", "Consumer", settingsFile))
                .Build();
            options = configuration
                .GetSection(AnalyticsEventConsumerOptions.SectionName)
                .Get<AnalyticsEventConsumerOptions>()!;
        }

        Assert.Equal(60, options.ReadTimeoutSeconds);
        Assert.Equal(30, options.PublishTimeoutSeconds);
        Assert.Equal(150, options.VisibilityTimeoutSeconds);
        Assert.True(options.HasValidProcessingBudget);
        Assert.False((options with { VisibilityTimeoutSeconds = 134 }).HasValidProcessingBudget);
    }

    [Fact]
    public void AddAnalyticsEventConsumer_ShouldRegisterTheConsumerAndOptions()
    {
        var services = new ServiceCollection();

        services.AddAnalyticsEventConsumer(CreateConfiguration());
        using var serviceProvider = services.BuildServiceProvider();

        Assert.Contains(
            services,
            service =>
                service.ServiceType == typeof(IHostedService)
                && service.ImplementationType == typeof(AnalyticsEventConsumer)
        );
        Assert.Contains(services, service => service.ServiceType == typeof(IAmazonSQS));

        var options = serviceProvider.GetRequiredService<IOptions<AnalyticsEventConsumerOptions>>().Value;

        Assert.Equal("http://localhost:4566/000000000000/analytics-events", options.QueueUrl);
        Assert.True(options.ProcessingEnabled);
        Assert.Equal(3, options.WaitTimeSeconds);
        Assert.Equal(4, options.PollIntervalSeconds);
    }

    [Fact]
    public void WhenAnalyticsIsDisabled_ShouldAllowUnconfiguredReadCredentials()
    {
        var configuration = CreateConfiguration();
        configuration["AnalyticsEventConsumer:ProcessingEnabled"] = "false";
        configuration["WasteObligations:BaseAddress"] = null;
        configuration["WasteObligations:Username"] = null;
        configuration["WasteObligations:Password"] = null;
        var services = new ServiceCollection().AddAnalyticsEventConsumer(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.False(provider.GetRequiredService<IOptions<WasteObligationsOptions>>().Value.HasReadAccess);
    }

    [Fact]
    public void WhenAnalyticsIsEnabledWithoutReadCredentials_ShouldRejectConfiguration()
    {
        var configuration = CreateConfiguration();
        configuration["WasteObligations:Password"] = null;
        var services = new ServiceCollection().AddAnalyticsEventConsumer(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<WasteObligationsOptions>>().Value
        );
    }

    [Theory]
    [InlineData("BaseAddress", "ftp://declarations.test")]
    [InlineData("BaseAddress", "http://declarations.test")]
    [InlineData("Username", "test:read-client")]
    [InlineData("DirectEnglishTemplateId", "not-a-template-uuid")]
    [InlineData("DirectEnglishTemplateId", "{5f64e3bd-d454-4a45-a9c6-9409bf940d7a}")]
    [InlineData("DirectWelshTemplateId", "not-a-template-uuid")]
    [InlineData("SchemeEnglishTemplateId", "not-a-template-uuid")]
    [InlineData("SchemeWelshTemplateId", "not-a-template-uuid")]
    public void WhenSubmissionConfigurationIsInvalid_ShouldRejectAnalyticsStartup(string property, string value)
    {
        var configuration = CreateConfiguration();
        var readSetting = property is "BaseAddress" or "Username";
        configuration[
            $"{(readSetting ? "WasteObligations" : "Notifications:Definitions:DeclarationSubmitted:Templates")}:{property}"
        ] = value;
        var services = new ServiceCollection().AddAnalyticsEventConsumer(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
        {
            if (readSetting)
                _ = provider.GetRequiredService<IOptions<WasteObligationsOptions>>().Value;
            else
                _ = provider.GetRequiredService<IOptions<NotificationDefinitionsOptions>>().Value;
        });
    }

    [Theory]
    [InlineData("WaitTimeSeconds", "0")]
    [InlineData("VisibilityTimeoutSeconds", "74")]
    [InlineData("ReceiveTimeoutSeconds", "3")]
    [InlineData("ReadTimeoutSeconds", "0")]
    [InlineData("PublishTimeoutSeconds", "0")]
    [InlineData("DeleteTimeoutSeconds", "0")]
    public void WhenAnalyticsBudgetCannotCoverProcessing_ShouldRejectConfiguration(string property, string value)
    {
        var configuration = CreateConfiguration();
        configuration[$"AnalyticsEventConsumer:{property}"] = value;
        var services = new ServiceCollection().AddAnalyticsEventConsumer(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<AnalyticsEventConsumerOptions>>().Value
        );
    }

    [Theory]
    [InlineData("http://localhost/api")]
    [InlineData("https://declarations.test/api")]
    [InlineData("https://declarations.test/api/")]
    public async Task WhenReadBaseAddressHasPathPrefix_ShouldPreservePrefixInRegisteredClient(string address)
    {
        const string organisationId = "fbf1ba88-cd09-42f6-aad5-0ddfc29e5765";
        const string declarationId = "65f1f6570bb08052a8a27b01";
        var configuration = CreateConfiguration();
        configuration["WasteObligations:BaseAddress"] = address;
        using var handler = new ReadHandler(request =>
        {
            Assert.Equal(
                address.StartsWith("https", StringComparison.Ordinal) ? HttpVersion.Version20 : HttpVersion.Version11,
                request.Version
            );
            Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
            Assert.Equal(
                $"/api/organisations/{organisationId}/compliance-declarations/{declarationId}",
                request.RequestUri!.AbsolutePath
            );
            Assert.Equal(
                "Basic dGVzdC1yZWFkLWNsaWVudDp0ZXN0LXJlYWQtc2VjcmV0",
                request.Headers.Authorization!.ToString()
            );

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"version\":1}") };
        });
        var services = new ServiceCollection().AddAnalyticsEventConsumer(configuration);
        services
            .AddHttpClient<IWasteObligationsClient, WasteObligationsClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IWasteObligationsClient>();

        var result = await reader.ReadDeclaration(
            Guid.Parse(organisationId),
            declarationId,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, result!.Version);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task WhenDeclarationIsNotFound_ShouldReturnNullWithoutRetrying()
    {
        using var handler = new ReadHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var services = new ServiceCollection().AddAnalyticsEventConsumer(CreateConfiguration());
        services
            .AddHttpClient<IWasteObligationsClient, WasteObligationsClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IWasteObligationsClient>();

        var result = await reader.ReadDeclaration(
            Guid.NewGuid(),
            "65f1f6570bb08052a8a27b01",
            TestContext.Current.CancellationToken
        );

        Assert.Null(result);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task WhenDeclarationReadHasTransientFailure_ShouldRetryGetAndReturnData()
    {
        var attempts = 0;
        using var handler = new ReadHandler(_ =>
        {
            attempts++;

            return attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"version\":1}") };
        });
        var services = new ServiceCollection().AddAnalyticsEventConsumer(CreateConfiguration());
        services
            .AddHttpClient<IWasteObligationsClient, WasteObligationsClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IWasteObligationsClient>();

        var result = await reader.ReadDeclaration(
            Guid.NewGuid(),
            "65f1f6570bb08052a8a27b01",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, result!.Version);
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task WhenRetryLimitIsConfigured_ShouldUseConfiguredAttempts(int retries)
    {
        var configuration = CreateConfiguration();
        configuration["WasteObligations:Retry:MaxRetryAttempts"] = retries.ToString();
        configuration["WasteObligations:Retry:Delay"] = "00:00:00";
        using var handler = new ReadHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAnalyticsEventConsumer(configuration);
        services
            .AddHttpClient<IWasteObligationsClient, WasteObligationsClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IWasteObligationsClient>();

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            reader.ReadDeclaration(Guid.NewGuid(), "65f1f6570bb08052a8a27b01", TestContext.Current.CancellationToken)
        );

        Assert.Equal(retries + 1, handler.RequestCount);
    }

    [Fact]
    public async Task WhenTransientReadExceptionContainsPrivateData_ShouldRetryWithoutLoggingIt()
    {
        const string privateData = "secret-basic-password private-recipient@example.test cdec_private-id";
        var attempts = 0;
        using var logs = new CapturedLogs();
        using var handler = new ReadHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
                throw new HttpRequestException(privateData);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"version\":1}") };
        });
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddAnalyticsEventConsumer(CreateConfiguration());
        services
            .AddHttpClient<IWasteObligationsClient, WasteObligationsClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IWasteObligationsClient>();

        var result = await reader.ReadDeclaration(
            Guid.NewGuid(),
            "65f1f6570bb08052a8a27b01",
            TestContext.Current.CancellationToken
        );
        provider.GetRequiredService<ILogger<AnalyticsEventConsumer>>().LogWarning("Fixed analytics retry diagnostic");

        Assert.Equal(1, result!.Version);
        Assert.Equal(2, handler.RequestCount);
        Assert.Contains("Fixed analytics retry diagnostic", logs.Messages);
        provider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Polly.Unrelated")
            .LogWarning("Other pipeline diagnostic");
        Assert.Contains("Other pipeline diagnostic", logs.Messages);
        Assert.All(
            logs.Messages,
            message =>
            {
                Assert.DoesNotContain("secret-basic-password", message, StringComparison.Ordinal);
                Assert.DoesNotContain("private-recipient@example.test", message, StringComparison.Ordinal);
                Assert.DoesNotContain("cdec_private-id", message, StringComparison.Ordinal);
            }
        );
    }

    [Fact]
    public async Task WhenDefaultHttpFailureLoggerIsRestored_ShouldDemonstratePrivateExceptionDisclosure()
    {
        const string privateData = "synthetic-private-dependency-message@example.test";
        var attempts = 0;
        using var logs = new CapturedLogs();
        using var handler = new ReadHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
                throw new HttpRequestException(privateData);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"version\":1}") };
        });
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddAnalyticsEventConsumer(CreateConfiguration());
        services
            .AddHttpClient<IWasteObligationsClient, WasteObligationsClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddDefaultLogger();
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IWasteObligationsClient>();

        var result = await reader.ReadDeclaration(
            Guid.NewGuid(),
            "65f1f6570bb08052a8a27b01",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, result!.Version);
        Assert.Equal(2, handler.RequestCount);
        Assert.Contains(
            logs.Exceptions,
            entry =>
                entry.Category.StartsWith("System.Net.Http.HttpClient.", StringComparison.Ordinal)
                && entry.Exception.Message == privateData
        );
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public List<(string Category, Exception Exception)> Exceptions { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturedLogger(categoryName, Messages, Exceptions);

        public void Dispose() { }
    }

    private sealed class CapturedLogger(
        string category,
        List<string> messages,
        List<(string Category, Exception Exception)> exceptions
    ) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            messages.Add(formatter(state, exception) + exception);
            if (exception is not null)
                exceptions.Add((category, exception));
        }
    }

    private static string RepositoryRoot()
    {
        for (
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
            if (File.Exists(Path.Combine(directory.FullName, "waste-obligations-notifications.slnx")))
                return directory.FullName;

        throw new InvalidOperationException("Repository root was not found for submission configuration.");
    }

    private sealed class ReadHandler(Func<HttpRequestMessage, HttpResponseMessage> read) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestCount++;

            return Task.FromResult(read(request));
        }
    }

    private static IConfiguration CreateConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["WasteOrganisations:BaseAddress"] = "https://organisations.test/",
            ["WasteOrganisations:Username"] = "isolated-reader",
            ["WasteOrganisations:Password"] = "synthetic-read-secret",
            ["AccountBackend:BaseAddress"] = "https://accounts.test/",
            ["AccountBackend:TokenEndpoint"] = "https://accounts.test/token",
            ["AccountBackend:ClientId"] = "isolated-reader",
            ["AccountBackend:ClientSecret"] = "synthetic-read-secret",
            ["WasteObligations:BaseAddress"] = "https://declarations.test",
            ["WasteObligations:Username"] = "test-read-client",
            ["WasteObligations:Password"] = "test-read-secret",
            [$"{AnalyticsEventConsumerOptions.SectionName}:QueueUrl"] =
                "http://localhost:4566/000000000000/analytics-events",
            [$"{AnalyticsEventConsumerOptions.SectionName}:ProcessingEnabled"] = "true",
            [$"{AnalyticsEventConsumerOptions.SectionName}:BatchSize"] = "2",
            [$"{AnalyticsEventConsumerOptions.SectionName}:WaitTimeSeconds"] = "3",
            [$"{AnalyticsEventConsumerOptions.SectionName}:PollIntervalSeconds"] = "4",
        };

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
