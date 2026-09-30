namespace Defra.WasteObligations.Consumer.Commands;

public static class UtcTimestamp
{
    public static bool HasExplicitUtcOffset(string? timestamp) =>
        timestamp is not null
        && (
            timestamp.EndsWith('Z')
            || timestamp.EndsWith("+00:00", StringComparison.Ordinal)
            || timestamp.EndsWith("-00:00", StringComparison.Ordinal)
        );
}
