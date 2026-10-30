using System.Text.Json;
using Defra.WasteObligations.Consumer.Notifications;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed class NotSubmittedReminderPresentation(
    NotSubmittedReminderCampaign campaign,
    IOptions<NotSubmittedReminderOptions> reminderOptions,
    IOptions<NotificationDefinitionsOptions> options
)
{
    public NotSubmittedReminderMessage Create(
        NotSubmittedReminderRecipient recipient,
        int obligationYear,
        string normalisedBusinessCountry,
        string regulatorEmail
    )
    {
        var welsh = WelshOrganisation.IsWelsh(normalisedBusinessCountry);
        var notificationType = $"{campaign.NotificationTypePrefix}-{(welsh ? "cy" : "en")}";
        var templateKey = welsh
            ? DeclarationNotificationTemplates.NotSubmittedReminder.WelshTemplateId
            : DeclarationNotificationTemplates.NotSubmittedReminder.EnglishTemplateId;
        var templateId = options.Value.GetTemplate(campaign.NotificationDefinition, templateKey);

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["firstName"] = recipient.FirstName,
            ["lastName"] = recipient.LastName,
            ["regulatorEmail"] = regulatorEmail,
        };
        foreach (var (key, value) in campaign.YearPersonalisation(obligationYear))
            values[key] = value;
        foreach (var (key, value) in reminderOptions.Value.Personalisation)
            values.TryAdd(key, value);

        var personalisation = JsonSerializer.SerializeToElement(values);

        return new NotSubmittedReminderMessage(notificationType, templateId, personalisation);
    }
}
