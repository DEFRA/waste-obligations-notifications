using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Defra.WasteObligations.Consumer.Commands;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Delivery;

public sealed class NotificationCommandDigest(IOptions<NotificationCommandDeliveryOptions> options)
    : INotificationCommandDigest
{
    public string CreateIdempotencyKeyDigest(string idempotencyKey) =>
        CreateDigest(options.Value.EvidenceDigestSecret, "idempotency-key", idempotencyKey);

    public string CreateImmutableFieldsDigest(NotificationCommand command)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "actionOccurredAtUtc",
                command.ActionOccurredAtUtc.ToString("O", CultureInfo.InvariantCulture)
            );
            writer.WriteString("emailAddress", command.EmailAddress.Trim().ToLowerInvariant());
            writer.WriteString("notificationType", command.NotificationType);
            writer.WriteString("templateId", command.TemplateId);
            writer.WritePropertyName("personalisation");
            WriteCanonicalJson(writer, command.Personalisation);
            writer.WriteEndObject();
        }

        return CreateDigest(
            options.Value.EvidenceDigestSecret,
            "immutable-fields",
            Encoding.UTF8.GetString(stream.ToArray())
        );
    }

    public string CreateRecipientDigest(string emailAddress) =>
        CreateDigest(options.Value.EvidenceDigestSecret, "recipient", emailAddress.Trim().ToLowerInvariant());

    public string CreateRecipientLane(string emailAddress) =>
        CreateDigest(options.Value.RecipientLaneSecret, "recipient-lane", emailAddress.Trim().ToLowerInvariant());

    private static string CreateDigest(string secret, string purpose, string value)
    {
        if (secret.StartsWith("set-automatically", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Notification command digest secret has not been configured.");
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes($"v1:{purpose}:{value}"));

        return $"v1:{Convert.ToHexStringLower(bytes)}";
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (
                    var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal)
                )
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var value in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, value);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
