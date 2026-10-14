namespace Defra.WasteObligations.Consumer.Services.WasteObligations;

public sealed record WasteObligationsOptions
{
    public const string SectionName = "WasteObligations";

    public string BaseAddress { get; init; } = "set-automatically-when-deployed";
    public string Username { get; init; } = "set-automatically-when-deployed";
    public string Password { get; init; } = "set-automatically-when-deployed";

    public bool HasReadAccess =>
        Uri.TryCreate(BaseAddress, UriKind.Absolute, out var uri)
        && (uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback))
        && Configured(Username)
        && Configured(Password)
        && !Username.Contains(':');

    private static bool Configured(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.StartsWith("set-automatically", StringComparison.Ordinal);
}
