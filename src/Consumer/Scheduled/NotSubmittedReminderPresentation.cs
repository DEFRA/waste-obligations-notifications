using System.Text.Json;
using Defra.WasteObligations.Consumer.Notifications;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed class NotSubmittedReminderPresentation(IOptions<NotificationDefinitionsOptions> options)
{
    public (string NotificationType, string TemplateId, JsonElement Personalisation) Create(
        NotSubmittedReminderRecipient recipient,
        int obligationYear,
        string normalisedBusinessCountry,
        string regulatorEmail
    )
    {
        var welsh = WelshOrganisation.IsWelsh(normalisedBusinessCountry);
        var notificationType = welsh
            ? "declaration-not-submitted-reminder-cy"
            : "declaration-not-submitted-reminder-en";
        var templateKey = welsh
            ? DeclarationNotificationTemplates.NotSubmittedReminder.WelshTemplateId
            : DeclarationNotificationTemplates.NotSubmittedReminder.EnglishTemplateId;
        var templateId = options.Value.GetTemplate(
            NotificationDefinitionsOptions.DeclarationNotSubmittedReminder,
            templateKey
        );
        var personalisation = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["firstName"] = recipient.FirstName,
                ["lastName"] = recipient.LastName,
                ["year"] = checked(obligationYear + 1),
                ["regulatorEmail"] = regulatorEmail,
            }
        );

        return (notificationType, templateId, personalisation);
    }
}
