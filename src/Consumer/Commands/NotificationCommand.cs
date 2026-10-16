using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Defra.WasteObligations.Consumer.Data;

namespace Defra.WasteObligations.Consumer.Commands;

public sealed record NotificationCommand(
    int SchemaVersion,
    string IdempotencyKey,
    [property: JsonConverter(typeof(UtcDateTimeOffsetConverter))] DateTimeOffset ActionOccurredAtUtc,
    string NotificationType,
    string EmailAddress,
    string TemplateId,
    JsonElement Personalisation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResendOfNotificationKey = null
)
{
    public const int CurrentSchemaVersion = 1;
    private static readonly SearchValues<char> s_opaqueKeyCharacters = SearchValues.Create("0123456789abcdef");

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
        if (IdempotencyKey.Length > 128 || IdempotencyKey.Any(character => character is < '!' or > '~'))
        {
            throw new InvalidDataException(
                "Notification command idempotencyKey must be at most 128 characters using only SQS-supported ASCII letters, digits, or punctuation."
            );
        }

        RequireValue(NotificationType, "notificationType");
        RequireValue(EmailAddress, "emailAddress");
        RequireValue(TemplateId, "templateId");

        if (ResendOfNotificationKey is not null && !IsOpaqueNotificationKey(ResendOfNotificationKey))
        {
            throw new InvalidDataException("Notification command resendOfNotificationKey must be a valid opaque key.");
        }

        if (
            MongoDateTime.TruncateToMilliseconds(ActionOccurredAtUtc) == default
            || ActionOccurredAtUtc.Offset != TimeSpan.Zero
        )
        {
            throw new InvalidDataException("Notification command actionOccurredAtUtc must be a UTC timestamp.");
        }

        if (Personalisation.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Notification command personalisation must be an object.");
        }
    }

    internal static bool IsOpaqueNotificationKey(string value) =>
        value.Length == 67
        && value.StartsWith("v1:", StringComparison.Ordinal)
        && value.AsSpan(3).IndexOfAnyExcept(s_opaqueKeyCharacters) < 0;

    private static void RequireValue(string value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Notification command is missing required property '{propertyName}'.");
        }
    }
}
