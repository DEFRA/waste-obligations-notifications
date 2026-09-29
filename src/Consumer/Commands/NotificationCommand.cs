using System.Text.Json;

namespace Defra.WasteObligations.Consumer.Commands;

public sealed record NotificationCommand(
    int SchemaVersion,
    string IdempotencyKey,
    DateTimeOffset ActionOccurredAtUtc,
    string NotificationType,
    string EmailAddress,
    string TemplateId,
    JsonElement Personalisation
)
{
    public const int CurrentSchemaVersion = 1;

    public NotificationCommand NormaliseRecipient() =>
        this with
        {
            EmailAddress = EmailAddress.Trim().ToLowerInvariant(),
        };

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Notification command schema version '{SchemaVersion}' is not supported.");
        }

        RequireValue(IdempotencyKey, "idempotencyKey");
        RequireValue(NotificationType, "notificationType");
        RequireValue(EmailAddress, "emailAddress");
        RequireValue(TemplateId, "templateId");

        if (ActionOccurredAtUtc == default || ActionOccurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Notification command actionOccurredAtUtc must be a UTC timestamp.");
        }

        if (Personalisation.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Notification command personalisation must be an object.");
        }
    }

    private static void RequireValue(string value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Notification command is missing required property '{propertyName}'.");
        }
    }
}
