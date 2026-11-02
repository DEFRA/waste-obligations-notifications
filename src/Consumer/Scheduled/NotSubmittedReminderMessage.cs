using System.Text.Json;

namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed record NotSubmittedReminderMessage(
    string NotificationType,
    string TemplateId,
    JsonElement Personalisation
);
