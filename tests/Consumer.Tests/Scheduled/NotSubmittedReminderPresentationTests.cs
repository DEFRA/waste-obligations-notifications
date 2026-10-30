using System.Text.Json;
using Defra.WasteObligations.Consumer.Notifications;
using Defra.WasteObligations.Consumer.Scheduled;
using Microsoft.Extensions.Options;
using Xunit;

namespace Defra.WasteObligations.Consumer.Tests.Scheduled;

public sealed class NotSubmittedReminderPresentationTests
{
    private const string ReportUrl = "https://example.test/report-data";

    private static readonly NotSubmittedReminderRecipient Recipient = new("ann@example.test", "Ann", "Approved");

    [Fact]
    public void Create_WhenFirstReminder_ShouldKeepExistingTemplateAndPersonalisation()
    {
        var presentation = CreatePresentation(NotSubmittedReminderCampaign.Email1, []);

        var (notificationType, templateId, personalisation) = presentation.Create(
            Recipient,
            2026,
            "GB-ENG",
            "regulator@example.test"
        );

        Assert.Equal("declaration-not-submitted-reminder-en", notificationType);
        Assert.Equal("54acaa9f-93c4-4052-bd0d-f5bc9e122ce6", templateId);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["firstName"] = "Ann",
                ["lastName"] = "Approved",
                ["regulatorEmail"] = "regulator@example.test",
                ["year"] = "2027",
            },
            Read(personalisation)
        );
    }

    [Theory]
    [InlineData("GB-ENG", "declaration-not-submitted-reminder-2-en", "458cd86f-861b-46ad-b67f-560a3c78ee94")]
    [InlineData("GB-WLS", "declaration-not-submitted-reminder-2-cy", "5f9e4390-41d6-418a-99bd-d0035ba39bc2")]
    public void Create_WhenSecondReminder_ShouldUseSecondTemplatesAndConfiguredPersonalisation(
        string businessCountry,
        string expectedType,
        string expectedTemplate
    )
    {
        var presentation = CreatePresentation(
            NotSubmittedReminderCampaign.Email2,
            new() { ["reportPackagingDataAccountUrl"] = ReportUrl }
        );

        var (notificationType, templateId, personalisation) = presentation.Create(
            Recipient,
            2026,
            businessCountry,
            "regulator@example.test"
        );

        Assert.Equal(expectedType, notificationType);
        Assert.Equal(expectedTemplate, templateId);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["firstName"] = "Ann",
                ["lastName"] = "Approved",
                ["regulatorEmail"] = "regulator@example.test",
                ["obligationYear"] = "2026",
                ["certYear"] = "2027",
                ["reportPackagingDataAccountUrl"] = ReportUrl,
            },
            Read(personalisation)
        );
    }

    private static NotSubmittedReminderPresentation CreatePresentation(
        NotSubmittedReminderCampaign campaign,
        Dictionary<string, string> personalisation
    ) =>
        new(
            campaign,
            Options.Create(new NotSubmittedReminderOptions { Personalisation = personalisation }),
            Options.Create(new NotificationDefinitionsOptions())
        );

    private static Dictionary<string, string> Read(JsonElement personalisation) =>
        personalisation
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.ToString());
}
