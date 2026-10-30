using System.Security.Cryptography;
using System.Text;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;

namespace Defra.WasteObligations.Consumer.Scheduled;

public static class NotSubmittedReminderCommandFactory
{
    public static NotificationCommand Create(
        NotSubmittedReminderCampaign campaign,
        int obligationYear,
        Guid organisationId,
        DateTimeOffset actionOccurredAtUtc,
        NotSubmittedReminderMessage message,
        string emailAddress
    )
    {
        var command = new NotificationCommand(
            NotificationCommand.CurrentSchemaVersion,
            string.Empty,
            MongoDateTime.TruncateToMilliseconds(actionOccurredAtUtc),
            message.NotificationType,
            emailAddress,
            message.TemplateId,
            message.Personalisation
        ).NormaliseRecipient();
        var emailHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(command.EmailAddress)));
        command = command with { IdempotencyKey = $"{campaign.Name}:{obligationYear}:{organisationId:N}:{emailHash}" };
        command.Validate();

        return command;
    }
}
