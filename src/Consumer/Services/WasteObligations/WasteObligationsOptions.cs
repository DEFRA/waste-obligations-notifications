using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Defra.WasteObligations.Consumer.Services.WasteObligations;

public sealed class WasteObligationsOptions : HttpStandardResilienceOptions
{
    public const string SectionName = "WasteObligations";

    public WasteObligationsOptions()
    {
        TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
        AttemptTimeout.Timeout = TimeSpan.FromSeconds(3);
        Retry.MaxRetryAttempts = 2;
        Retry.Delay = TimeSpan.FromMilliseconds(200);
        Retry.BackoffType = DelayBackoffType.Exponential;
        Retry.UseJitter = true;
        Retry.DisableForUnsafeHttpMethods();
    }

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
