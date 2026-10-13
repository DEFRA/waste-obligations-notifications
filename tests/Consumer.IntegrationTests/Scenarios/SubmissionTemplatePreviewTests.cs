using System.Net;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Submission;
using Microsoft.Extensions.Configuration;
using Notify.Client;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class SubmissionTemplatePreviewTests
{
    [Theory]
    [InlineData("direct-en")]
    [InlineData("direct-cy")]
    [InlineData("scheme-en")]
    [InlineData("scheme-cy")]
    public async Task WhenConfiguredSubmissionTemplateIsPreviewed_ShouldRenderExpectedContent(string variant)
    {
        var apiKey = Environment.GetEnvironmentVariable("GOVUKNOTIFY_APIKEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Assert.False(
                Environment.GetEnvironmentVariable("NOTIFY_PREVIEWS_REQUIRED") == "true",
                "Required Notify previews need GOVUKNOTIFY_APIKEY."
            );
            Assert.Skip("Notify previews were not executed: GOVUKNOTIFY_APIKEY is absent.");
        }
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("consumer-appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();
        var options =
            configuration.GetSection(SubmissionEmailOptions.SectionName).Get<SubmissionEmailOptions>()
            ?? new SubmissionEmailOptions();
        Assert.True(options.HasValidTemplates);
        var templateId = variant switch
        {
            "direct-en" => options.DirectEnglishTemplateId,
            "direct-cy" => options.DirectWelshTemplateId,
            "scheme-en" => options.SchemeEnglishTemplateId,
            "scheme-cy" => options.SchemeWelshTemplateId,
            _ => throw new InvalidOperationException("Unknown submission preview variant."),
        };
        var welsh = variant.EndsWith("-cy", StringComparison.Ordinal);
        var personalisation = new Dictionary<string, object>
        {
            ["obligationYear"] = 2026,
            ["regulatorLeading"] = welsh ? "Regulator" : "The Regulator",
            ["regulatorInline"] = welsh ? "Regulator" : "the Regulator",
            ["regulatorEmail"] = "regulator@email.com",
            ["user"] = "Submitter Name",
        };
        // Only preview: no email send and no fixture endpoint or fixture credentials.
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var http = new HttpClient
            {
                BaseAddress = new Uri(new NotifyOptions().BaseAddress),
                Timeout = Timeout.InfiniteTimeSpan,
            };
            using var transport = new NotifySdkHttpClient(http, HttpStatusCode.OK, deadline.Token);
            var client = new NotificationClient(transport, apiKey);
            var preview = await Task.Run(
                    () => client.GenerateTemplatePreviewAsync(templateId, personalisation),
                    deadline.Token
                )
                .WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            Assert.NotNull(preview);
            Assert.Contains("2026", preview.subject, StringComparison.Ordinal);
            Assert.Contains("Regulator", preview.subject, StringComparison.Ordinal);
            Assert.Contains("2026", preview.body, StringComparison.Ordinal);
            Assert.Contains("Regulator", preview.body, StringComparison.Ordinal);
            Assert.Contains("regulator@email.com", preview.body, StringComparison.Ordinal);
            Assert.Contains("Submitter Name", preview.body, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            // SDK errors can include credential or provider response content; retain only the variant.
            Assert.Fail($"Notify submission preview failed for {variant}: provider error or unexpected rendering.");
        }
    }
}
