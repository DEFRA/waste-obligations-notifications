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
