using System.Net;
using Amazon.SQS;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Submission;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Tests.Consumers;

public class ServiceCollectionExtensionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void WhenSubmissionOptionsUseDefaults_ShouldPreserveEachLegacyTemplateMapping(string? settingsFile)
    {
        var options = new SubmissionOptions();
        if (settingsFile is not null)
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(RepositoryRoot(), "src", "Consumer", settingsFile))
                .Build();
            options = configuration.GetSection(SubmissionOptions.SectionName).Get<SubmissionOptions>()!;
        }

        Assert.Equal("5f64e3bd-d454-4a45-a9c6-9409bf940d7a", options.DirectEnglishTemplateId);
        Assert.Equal("b3223b0b-a467-40c1-9150-f78b76d11fd8", options.DirectWelshTemplateId);
        Assert.Equal("b103685d-de8a-4ea9-abc8-d244ca26841b", options.SchemeEnglishTemplateId);
        Assert.Equal("95d8c2aa-a229-4ecb-9774-78ec06d0cbac", options.SchemeWelshTemplateId);
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
        configuration["Submission:BaseAddress"] = null;
        configuration["Submission:ClientId"] = null;
        configuration["Submission:ClientSecret"] = null;
        var services = new ServiceCollection().AddAnalyticsEventConsumer(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.False(provider.GetRequiredService<IOptions<SubmissionOptions>>().Value.HasReadAccess);
    }

    [Fact]
    public void WhenAnalyticsIsEnabledWithoutReadCredentials_ShouldRejectConfiguration()
    {
        var configuration = CreateConfiguration();
        configuration["Submission:ClientSecret"] = null;
        var services = new ServiceCollection().AddAnalyticsEventConsumer(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<SubmissionOptions>>().Value
        );
    }

    [Theory]
    [InlineData("BaseAddress", "ftp://declarations.test")]
    [InlineData("ClientId", "test:read-client")]
    public void WhenReadAccessConfigurationIsInvalid_ShouldRejectAnalyticsStartup(string property, string value)
    {
        var configuration = CreateConfiguration();
        configuration[$"Submission:{property}"] = value;
        var services = new ServiceCollection().AddAnalyticsEventConsumer(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<SubmissionOptions>>().Value
        );
    }

    [Theory]
    [InlineData("DirectEnglishTemplateId")]
    [InlineData("DirectWelshTemplateId")]
    [InlineData("SchemeEnglishTemplateId")]
    [InlineData("SchemeWelshTemplateId")]
    public void WhenAnyTemplateMappingIsInvalid_ShouldRejectConfiguration(string property)
    {
        var configuration = CreateConfiguration();
        configuration[$"Submission:{property}"] = "not-a-template-uuid";
        var services = new ServiceCollection().AddAnalyticsEventConsumer(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<SubmissionOptions>>().Value
        );
    }

    [Theory]
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
    [InlineData("http://declarations.test/api")]
    [InlineData("http://declarations.test/api/")]
    public async Task WhenReadBaseAddressHasPathPrefix_ShouldPreservePrefixInRegisteredClient(string address)
    {
        const string organisationId = "fbf1ba88-cd09-42f6-aad5-0ddfc29e5765";
        const string declarationId = "65f1f6570bb08052a8a27b01";
        var configuration = CreateConfiguration();
        configuration["Submission:BaseAddress"] = address;
        using var handler = new ReadHandler(request =>
        {
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
            .AddHttpClient<IDeclarationReadClient, DeclarationReadClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IDeclarationReadClient>();

        var result = await reader.Read(
            Guid.Parse(organisationId),
            declarationId,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, result.GetProperty("version").GetInt32());
        Assert.Equal(1, handler.RequestCount);
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
            ["Submission:BaseAddress"] = "http://declarations.test",
            ["Submission:ClientId"] = "test-read-client",
            ["Submission:ClientSecret"] = "test-read-secret",
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
