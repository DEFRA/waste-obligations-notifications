using Defra.WasteObligations.Consumer.IntegrationTests.Helpers;
using Defra.WasteObligations.Consumer.Notifications;
using Microsoft.Extensions.Configuration;
using Notify.Client;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class NotifyReminderPreviewTests
{
    [Fact]
    public Task EnglishEnvironmentAgency_ShouldRenderReminder() =>
        Preview(
            DeclarationNotificationTemplates.NotSubmittedReminder.EnglishTemplateId,
            "packagingproducers@environment-agency.gov.uk",
            expectWelshCopy: false
        );

    [Fact]
    public Task EnglishSepa_ShouldRenderReminder() =>
        Preview(
            DeclarationNotificationTemplates.NotSubmittedReminder.EnglishTemplateId,
            "producer.responsibility@sepa.org.uk",
            expectWelshCopy: false
        );

    [Fact]
    public Task EnglishNrw_ShouldRenderReminder() =>
        Preview(
            DeclarationNotificationTemplates.NotSubmittedReminder.EnglishTemplateId,
            "packaging@naturalresourceswales.gov.uk",
            expectWelshCopy: false
        );

    [Fact]
    public Task EnglishNiea_ShouldRenderReminder() =>
        Preview(
            DeclarationNotificationTemplates.NotSubmittedReminder.EnglishTemplateId,
            "packaging@daera-ni.gov.uk",
            expectWelshCopy: false
        );

    [Fact]
    public Task WelshBilingual_ShouldRenderReminder() =>
        Preview(
            DeclarationNotificationTemplates.NotSubmittedReminder.WelshTemplateId,
            "packaging@naturalresourceswales.gov.uk",
            expectWelshCopy: true
        );

    private static async Task Preview(string mapping, string regulatorEmail, bool expectWelshCopy)
    {
        var (configuration, key) = NotifyPreviewSettings.Load();
        var options =
            configuration.GetSection(NotificationDefinitionsOptions.SectionName).Get<NotificationDefinitionsOptions>()
            ?? new NotificationDefinitionsOptions();
        var template = options.GetTemplate(NotificationDefinitionsOptions.DeclarationNotSubmittedReminder, mapping);
        var personalisation = new Dictionary<string, object>
        {
            ["firstName"] = "SyntheticFirst",
            ["lastName"] = "SyntheticLast",
            ["year"] = 2027,
            ["regulatorEmail"] = regulatorEmail,
        };
        string body;
        try
        {
            var client = new NotificationClient(key);
            var preview = await client.GenerateTemplatePreviewAsync(template, personalisation);
            body = preview.body;
        }
        catch (Exception)
        {
            Assert.Fail("Notify reminder preview failed; check credential, template access and provider availability.");

            return;
        }

        foreach (var value in new[] { "2027", regulatorEmail, "SyntheticFirst", "SyntheticLast" })
        {
            Assert.Contains(value, body, StringComparison.Ordinal);
        }

        if (expectWelshCopy)
            Assert.Contains("Annwyl", body, StringComparison.Ordinal);
    }
}
