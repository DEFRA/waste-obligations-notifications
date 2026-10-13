using System.Globalization;
using System.Net;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Startup;
using Defra.WasteObligations.Consumer.Submission;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests.Consumers;

public sealed class SubmissionRegistrationTests
{
    [Fact]
    public async Task WhenAnalyticsIsDisabled_ShouldResolveAndStartWithDeclarationPlaceholdersWithoutQueueEffects()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAnalyticsEventConsumer(Configuration(new Dictionary<string, string?>()));
        var sqs = Substitute.For<IAmazonSQS>();
        services.AddSingleton(sqs);
        services.AddSingleton(
            Options.Create(
                new NotificationCommandDeliveryOptions
                {
                    QueueUrl = "http://localhost/commands.fifo",
                    EvidenceDigestSecret = "test-evidence-secret",
                    RecipientLaneSecret = "test-lane-secret",
                }
            )
        );
        services.AddSingleton<INotificationCommandDigest, NotificationCommandDigest>();
        services.AddSingleton<INotificationCommandPublisher, NotificationCommandPublisher>();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ApplicationStartup>().MarkStarted();
        Assert.False(provider.GetRequiredService<IOptions<SubmissionEmailOptions>>().Value.HasReadConfiguration);
        var consumer = Assert.IsType<AnalyticsEventConsumer>(Assert.Single(provider.GetServices<IHostedService>()));

        await consumer.StartAsync(TestContext.Current.CancellationToken);
        await consumer.StopAsync(TestContext.Current.CancellationToken);

        await sqs.DidNotReceive().ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("SubmissionEmail:ApiBaseAddress", "set-automatically-when-deployed")]
    [InlineData("SubmissionEmail:ApiBaseAddress", "file:///private")]
    [InlineData("SubmissionEmail:ClientId", "")]
    [InlineData("SubmissionEmail:ClientSecret", "set-automatically-when-deployed")]
    [InlineData("SubmissionEmail:DirectEnglishTemplateId", "")]
    [InlineData("SubmissionEmail:DirectWelshTemplateId", "invalid")]
    [InlineData("SubmissionEmail:SchemeEnglishTemplateId", "invalid")]
    [InlineData("SubmissionEmail:SchemeWelshTemplateId", "invalid")]
    public void WhenAnalyticsIsEnabled_ShouldRejectInvalidReadOrTemplateConfiguration(string key, string value)
    {
        var services = new ServiceCollection();
        services.AddAnalyticsEventConsumer(
            Configuration(
                new Dictionary<string, string?>
                {
                    ["AnalyticsEventConsumer:ProcessingEnabled"] = "true",
                    ["SubmissionEmail:ApiBaseAddress"] = "http://localhost/declarations",
                    ["SubmissionEmail:ClientId"] = "reader",
                    ["SubmissionEmail:ClientSecret"] = "test-secret",
                    [key] = value,
                }
            )
        );
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<SubmissionEmailOptions>>().Value
        );
    }

    [Theory]
    [InlineData(20, 30, 120, true)]
    [InlineData(20, 20, 120, false)]
    [InlineData(0, 30, 64, false)]
    [InlineData(0, 30, 65, true)]
    public void WhenAnalyticsBudgetIsConfigured_ShouldRequireFullAttemptAndHeadroomWithinVisibility(
        int wait,
        int receive,
        int visibility,
        bool valid
    )
    {
        var services = new ServiceCollection();
        services.AddAnalyticsEventConsumer(
            Configuration(
                new Dictionary<string, string?>
                {
                    ["AnalyticsEventConsumer:WaitTimeSeconds"] = wait.ToString(CultureInfo.InvariantCulture),
                    ["AnalyticsEventConsumer:ReceiveTimeoutSeconds"] = receive.ToString(CultureInfo.InvariantCulture),
                    ["AnalyticsEventConsumer:VisibilityTimeoutSeconds"] = visibility.ToString(CultureInfo.InvariantCulture),
                    ["AnalyticsEventConsumer:ReadTimeoutSeconds"] = "10",
                    ["AnalyticsEventConsumer:PublishTimeoutSeconds"] = "10",
                    ["AnalyticsEventConsumer:DeleteTimeoutSeconds"] = "5",
                    ["AnalyticsEventConsumer:SafetyHeadroomSeconds"] = "10",
                }
            )
        );
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AnalyticsEventConsumerOptions>>();

        if (valid)
        {
            Assert.Equal(visibility, options.Value.VisibilityTimeoutSeconds);
            Assert.Equal(10, options.Value.ReadTimeoutSeconds);
            Assert.Equal(10, options.Value.PublishTimeoutSeconds);
            Assert.Equal(5, options.Value.DeleteTimeoutSeconds);
            Assert.Equal(10, options.Value.SafetyHeadroomSeconds);
            Assert.True(options.Value.HasValidProcessingBudget);
        }
        else
            Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    [Fact]
    public void WhenSubmissionDefaultsAreUsed_ShouldRetainAllLegacyTemplateIds()
    {
        var services = new ServiceCollection();
        services.AddAnalyticsEventConsumer(Configuration(new Dictionary<string, string?>()));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<SubmissionEmailOptions>>().Value;

        Assert.Equal("5f64e3bd-d454-4a45-a9c6-9409bf940d7a", options.DirectEnglishTemplateId);
        Assert.Equal("b3223b0b-a467-40c1-9150-f78b76d11fd8", options.DirectWelshTemplateId);
        Assert.Equal("b103685d-de8a-4ea9-abc8-d244ca26841b", options.SchemeEnglishTemplateId);
        Assert.Equal("95d8c2aa-a229-4ecb-9774-78ec06d0cbac", options.SchemeWelshTemplateId);
    }

    [Fact]
    public void WhenSubmissionTemplatesAreOverridden_ShouldBindAllFourMappings()
    {
        var services = new ServiceCollection();
        services.AddAnalyticsEventConsumer(
            Configuration(
                new Dictionary<string, string?>
                {
                    ["SubmissionEmail:DirectEnglishTemplateId"] = "11111111-1111-4111-8111-111111111111",
                    ["SubmissionEmail:DirectWelshTemplateId"] = "22222222-2222-4222-8222-222222222222",
                    ["SubmissionEmail:SchemeEnglishTemplateId"] = "33333333-3333-4333-8333-333333333333",
                    ["SubmissionEmail:SchemeWelshTemplateId"] = "44444444-4444-4444-8444-444444444444",
                }
            )
        );
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<SubmissionEmailOptions>>().Value;

        Assert.Equal("11111111-1111-4111-8111-111111111111", options.DirectEnglishTemplateId);
        Assert.Equal("22222222-2222-4222-8222-222222222222", options.DirectWelshTemplateId);
        Assert.Equal("33333333-3333-4333-8333-333333333333", options.SchemeEnglishTemplateId);
        Assert.Equal("44444444-4444-4444-8444-444444444444", options.SchemeWelshTemplateId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenDeclarationHealthIsChecked_ShouldOnlyReadAnonymousHealthWhenAnalyticsIsEnabled(bool enabled)
    {
        var handler = new HealthHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://declarations") };
        var check = new DeclarationHealthCheck(
            new DeclarationClient(client, Options.Create(new SubmissionEmailOptions())),
            Options.Create(
                new AnalyticsEventConsumerOptions
                {
                    QueueUrl = "http://localhost/analytics",
                    ProcessingEnabled = enabled,
                }
            )
        );

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(enabled ? 1 : 0, handler.Requests);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values)
    {
        values["AnalyticsEventConsumer:QueueUrl"] = "http://localhost/analytics";

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed class HealthHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/health", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            Requests++;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
