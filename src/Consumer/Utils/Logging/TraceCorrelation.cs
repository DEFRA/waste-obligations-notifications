namespace Defra.WasteObligations.Consumer.Utils.Logging;

public static class TraceCorrelation
{
    public static string? Read(string? value) =>
        value is not null
        && (Guid.TryParseExact(value, "D", out _) || value.Length == 32 && value.All(char.IsAsciiHexDigit))
            ? value
            : null;
}
