using System.Text.Json;
using Defra.WasteObligations.Consumer.Data;

namespace Defra.WasteObligations.Consumer.Commands;

public static class UtcTimestamp
{
    public static bool TryParse(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (
            !HasExplicitUtcOffset(value)
            || !JsonSerializer.SerializeToElement(value).TryGetDateTimeOffset(out var parsed)
            || parsed.Offset != TimeSpan.Zero
        )
            return false;

        timestamp = MongoDateTime.TruncateToMilliseconds(parsed);

        return true;
    }

    private static bool HasExplicitUtcOffset(string? timestamp) =>
        timestamp is not null
        && (
            timestamp.EndsWith('Z')
            || timestamp.EndsWith("+00:00", StringComparison.Ordinal)
            || timestamp.EndsWith("-00:00", StringComparison.Ordinal)
        );
}
