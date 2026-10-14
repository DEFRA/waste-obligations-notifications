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
        var root = RepositoryRoot();
        var key = Environment.GetEnvironmentVariable("GOVUKNOTIFY_APIKEY") ?? LocalKey(root);
        if (string.IsNullOrWhiteSpace(key))
        {
            if (Environment.GetEnvironmentVariable("NOTIFY_PREVIEWS_REQUIRED") == "1")
                Assert.Fail("Required Notify preview credential is missing.");
            Assert.Skip("Notify previews were not executed: GOVUKNOTIFY_APIKEY is missing.");
        }
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root, "src/Consumer/appsettings.json"))
            .AddEnvironmentVariables()
            .Build();
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
            var client = new NotificationClient(key!);
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

    private static string RepositoryRoot()
    {
        for (
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
            if (File.Exists(Path.Combine(directory.FullName, "waste-obligations-notifications.slnx")))
                return directory.FullName;
        throw new InvalidOperationException("Repository root was not found for Notify preview configuration.");
    }

    private static string? LocalKey(string root)
    {
        var path = Path.Combine(root, ".env");
        if (!File.Exists(path))
            return null;
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Trim().Split('=', 2);
            if (parts.Length != 2 || parts[0].Trim() != "GovukNotify_ApiKey")
                continue;
            var value = parts[1].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"')
                value = value[1..^1];

            return value;
        }

        return null;
    }
}
