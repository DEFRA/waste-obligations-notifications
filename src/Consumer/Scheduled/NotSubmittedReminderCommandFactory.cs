using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;

namespace Defra.WasteObligations.Consumer.Scheduled;

public static class NotSubmittedReminderCommandFactory
{
    public const string CampaignId = "1";

    public static NotificationCommand Create(
        int obligationYear,
        Guid organisationId,
        DateTimeOffset actionOccurredAtUtc,
        string notificationType,
        string templateId,
        string emailAddress,
        JsonElement personalisation
    )
    {
        var command = new NotificationCommand(
            NotificationCommand.CurrentSchemaVersion,
            string.Empty,
            MongoDateTime.TruncateToMilliseconds(actionOccurredAtUtc),
            notificationType,
            emailAddress,
            templateId,
            personalisation
        ).NormaliseRecipient();
        var emailHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(command.EmailAddress)));
        command = command with
        {
            IdempotencyKey = $"not-submitted-reminder-{CampaignId}:{obligationYear}:{organisationId:N}:{emailHash}",
        };
        command.Validate();

        return command;
    }
}
