using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using MongoDB.Bson;

namespace Defra.WasteObligations.Consumer.Administration;

public sealed record DeliveryRecordSearchQuery
{
    public DateTime? RecordedFromUtc { get; init; }
    public DateTime? RecordedToUtc { get; init; }
    public string? NotifyNotificationId { get; init; }
    public string? NotificationKey { get; init; }
    public string? ResendOfNotificationKey { get; init; }
    public string? Outcome { get; init; }
    public string? SourceType { get; init; }
    public string? SourceId { get; init; }
    public string? RecipientDigest { get; init; }
    public int PageSize { get; init; } = 25;
    public string? Cursor { get; init; }

    public static DeliveryRecordSearchQuery Parse(IQueryCollection values)
    {
        string[] supportedNames =
        [
            "recordedFromUtc",
            "recordedToUtc",
            "notifyNotificationId",
            "notificationKey",
            "resendOfNotificationKey",
            "outcome",
            "sourceType",
            "sourceId",
            "pageSize",
            "cursor",
        ];
        if (values.Keys.Any(name => !supportedNames.Contains(name, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Delivery record search query is invalid.");

        var query = new DeliveryRecordSearchQuery
        {
            RecordedFromUtc = ReadUtc(Read(values, "recordedFromUtc")),
            RecordedToUtc = ReadUtc(Read(values, "recordedToUtc")),
            NotifyNotificationId = Read(values, "notifyNotificationId"),
            NotificationKey = Read(values, "notificationKey"),
            ResendOfNotificationKey = Read(values, "resendOfNotificationKey"),
            Outcome = Read(values, "outcome"),
            SourceType = Read(values, "sourceType"),
            SourceId = Read(values, "sourceId"),
            PageSize = Read(values, "pageSize") is { } size
                ? int.Parse(size, NumberStyles.None, CultureInfo.InvariantCulture)
                : 25,
            Cursor = Read(values, "cursor"),
        };
        query.Validate();

        return query;
    }

    public void Validate()
    {
        if (
            (SourceType is null) != (SourceId is null)
            || PageSize is < 1 or > 100
            || RecordedFromUtc is { Kind: not DateTimeKind.Utc }
            || RecordedToUtc is { Kind: not DateTimeKind.Utc }
            || RecordedFromUtc >= RecordedToUtc
            || new[]
            {
                NotifyNotificationId,
                NotificationKey,
                ResendOfNotificationKey,
                Outcome,
                SourceType,
                SourceId,
                RecipientDigest,
            }.Any(value => value is not null && (value.Length is < 1 or > 512 || value.Any(char.IsControl)))
        )
            throw new ArgumentException("Delivery record search query is invalid.");
        ReadCursor();
    }

    internal (DateTime RecordedAtUtc, ObjectId Id)? ReadCursor()
    {
        if (Cursor is null)
            return null;
        try
        {
            if (
                Cursor.Length is < 1 or > 512
                || Cursor.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
            )
                throw new FormatException();
            var parts = JsonSerializer.Deserialize<string[]>(WebEncoders.Base64UrlDecode(Cursor));
            if (
                parts is not { Length: 4 }
                || parts[0] != "1"
                || parts[3] != FilterFingerprint()
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks % TimeSpan.TicksPerMillisecond != 0
                || !ObjectId.TryParse(parts[2], out var id)
            )
                throw new FormatException();

            return (new DateTime(ticks, DateTimeKind.Utc), id);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            throw new ArgumentException("Delivery record search cursor is invalid.");
        }
    }

    internal string CreateCursor(DateTime recordedAtUtc, ObjectId id) =>
        WebEncoders.Base64UrlEncode(
            JsonSerializer.SerializeToUtf8Bytes(
                new[]
                {
                    "1",
                    recordedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                    id.ToString(),
                    FilterFingerprint(),
                }
            )
        );

    private string FilterFingerprint()
    {
        var filters = new Dictionary<string, object?>
        {
            [nameof(RecordedFromUtc)] = RecordedFromUtc,
            [nameof(RecordedToUtc)] = RecordedToUtc,
            [nameof(NotifyNotificationId)] = NotifyNotificationId,
            [nameof(NotificationKey)] = NotificationKey,
            [nameof(ResendOfNotificationKey)] = ResendOfNotificationKey,
            [nameof(Outcome)] = Outcome,
        };
        if (SourceType is not null)
            filters[nameof(SourceType)] = SourceType;
        if (SourceId is not null)
            filters[nameof(SourceId)] = SourceId;
        if (RecipientDigest is not null)
            filters[nameof(RecipientDigest)] = RecipientDigest;

        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(filters)));
    }

    private static string? Read(IQueryCollection values, string name)
    {
        if (!values.TryGetValue(name, out var value))
            return null;
        if (value.Count != 1 || string.IsNullOrEmpty(value[0]))
            throw new ArgumentException("Delivery record search query is invalid.");

        return value[0];
    }

    private static DateTime? ReadUtc(string? value)
    {
        if (value is null)
            return null;
        if (
            value.Length > 64
            || !(
                value.EndsWith('Z')
                || value.EndsWith("+00:00", StringComparison.Ordinal)
                || value.EndsWith("-00:00", StringComparison.Ordinal)
            )
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || parsed.Offset != TimeSpan.Zero
        )
            throw new ArgumentException("Delivery record search UTC bound is invalid.");

        return parsed.UtcDateTime;
    }
}
