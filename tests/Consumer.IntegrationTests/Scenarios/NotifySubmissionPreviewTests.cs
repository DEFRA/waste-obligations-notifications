using Defra.WasteObligations.Consumer.IntegrationTests.Helpers;
using Defra.WasteObligations.Consumer.Submission;
using Microsoft.Extensions.Configuration;
using Notify.Client;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class NotifySubmissionPreviewTests
{
    [Fact]
    public Task DirectEnglish_ShouldRenderSubmission() => Preview("DirectEnglishTemplateId", false);

    [Fact]
    public Task DirectWelsh_ShouldRenderSubmission() => Preview("DirectWelshTemplateId", true);

    [Fact]
    public Task SchemeEnglish_ShouldRenderSubmission() => Preview("SchemeEnglishTemplateId", false);

    [Fact]
    public Task SchemeWelsh_ShouldRenderSubmission() => Preview("SchemeWelshTemplateId", true);

    private static async Task Preview(string mapping, bool welsh)
    {
        var (configuration, key) = NotifyPreviewSettings.Load();
        var options =
            configuration.GetSection(SubmissionOptions.SectionName).Get<SubmissionOptions>() ?? new SubmissionOptions();
        var template = mapping switch
        {
            "DirectEnglishTemplateId" => options.DirectEnglishTemplateId,
            "DirectWelshTemplateId" => options.DirectWelshTemplateId,
            "SchemeEnglishTemplateId" => options.SchemeEnglishTemplateId,
            "SchemeWelshTemplateId" => options.SchemeWelshTemplateId,
            _ => throw new InvalidOperationException("Unknown submission template mapping."),
        };
        string subject;
        string body;
        try
        {
            var client = new NotificationClient(key);
            var preview = await client.GenerateTemplatePreviewAsync(
                template,
                new Dictionary<string, object>
                {
                    ["obligationYear"] = 2026,
                    ["regulatorLeading"] = welsh ? "Regulator" : "The Regulator",
                    ["regulatorInline"] = welsh ? "Regulator" : "the Regulator",
                    ["regulatorEmail"] = "regulator@email.com",
                    ["user"] = "Submitter Name",
                }
            );
            subject = preview.subject;
            body = preview.body;
        }
        catch (Exception)
        {
            Assert.Fail(
                "Notify submission preview failed; check credential, template access and provider availability."
            );
            return;
        }
        // Boolean assertions keep rendered content and provider responses out of reports.
        Assert.True(subject.Contains("2026", StringComparison.Ordinal), "Preview subject must contain the year.");
        Assert.True(
            subject.Contains("Regulator", StringComparison.Ordinal),
            "Preview subject must contain the regulator."
        );
        foreach (var value in new[] { "2026", "Regulator", "regulator@email.com", "Submitter Name" })
            Assert.True(
                body.Contains(value, StringComparison.Ordinal),
                "Preview body is missing required personalisation."
            );
    }
}
