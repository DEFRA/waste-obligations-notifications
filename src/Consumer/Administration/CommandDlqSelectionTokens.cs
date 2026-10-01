using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Administration;

public sealed class CommandDlqSelectionTokens(
    IOptions<CommandDlqAdministrationOptions> administration,
    IOptions<NotificationCommandDeliveryOptions> delivery,
    TimeProvider timeProvider
)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
    };

    public DateTimeOffset CreateExpiry() =>
        timeProvider.GetUtcNow().AddSeconds(administration.Value.SelectionLifetimeSeconds);

    public TimeSpan RemainingLifetime(CommandDlqSelection selection) =>
        selection.ExpiresAtUtc - timeProvider.GetUtcNow();

    public string Create(
        string receiveAttemptId,
        string messageId,
        DateTimeOffset expiresAtUtc,
        string immutableFieldsDigest
    )
    {
        var selection = new CommandDlqSelection(
            receiveAttemptId,
            messageId,
            QueueBinding(),
            expiresAtUtc,
            immutableFieldsDigest
        );
        var now = timeProvider.GetUtcNow();
        if (
            !IsValid(selection)
            || expiresAtUtc > now.AddSeconds(administration.Value.SelectionLifetimeSeconds)
            || expiresAtUtc >= now.AddMinutes(5)
        )
            throw new InvalidOperationException("Command DLQ selection is invalid or expired.");
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(selection, s_jsonOptions));

        return $"v1.{payload}.{Encode(Sign("selection", payload))}";
    }

    public CommandDlqSelection? Validate(string? token)
    {
        if (token is null || token.Length > 4096)
            return null;
        try
        {
            var parts = token.Split('.');
            if (
                parts.Length != 3
                || parts[0] != "v1"
                || !CryptographicOperations.FixedTimeEquals(Sign("selection", parts[1]), Decode(parts[2]))
            )
                return null;
            var selection = JsonSerializer.Deserialize<CommandDlqSelection>(Decode(parts[1]), s_jsonOptions);

            return selection is not null && IsValid(selection) ? selection : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool IsValid(CommandDlqSelection selection)
    {
        var now = timeProvider.GetUtcNow();

        return Guid.TryParse(selection.ReceiveRequestAttemptId, out _)
            && selection.MessageId is { Length: > 0 and <= 100 }
            && selection.QueueBinding == QueueBinding()
            && selection.ExpiresAtUtc.Offset == TimeSpan.Zero
            && selection.ExpiresAtUtc > now
            && selection.ImmutableFieldsDigest is { Length: 67 }
            && selection.ImmutableFieldsDigest.StartsWith("v1:", StringComparison.Ordinal)
            && selection
                .ImmutableFieldsDigest[3..]
                .All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private string QueueBinding() => $"v1:{Convert.ToHexStringLower(Sign("queue", administration.Value.QueueUrl))}";

    private byte[] Sign(string purpose, string value)
    {
        if (!NotificationCommandDeliveryOptions.IsSecretConfigured(delivery.Value.EvidenceDigestSecret))
            throw new InvalidOperationException("Command DLQ selection secret has not been configured.");

        return HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(delivery.Value.EvidenceDigestSecret),
            Encoding.UTF8.GetBytes($"v1:command-dlq-{purpose}:{value}")
        );
    }

    private static string Encode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
    }
}
