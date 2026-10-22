using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Defra.WasteObligations.Consumer.Services.Http;

public abstract class DownstreamOptions : HttpStandardResilienceOptions
{
    protected DownstreamOptions()
    {
        TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
        AttemptTimeout.Timeout = TimeSpan.FromSeconds(3);
        Retry.MaxRetryAttempts = 2;
        Retry.Delay = TimeSpan.FromMilliseconds(200);
        Retry.BackoffType = DelayBackoffType.Exponential;
        Retry.UseJitter = true;
    }

    public string BaseAddress { get; init; } = "set-automatically-when-deployed";

    public static bool SecureAddress(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback))
        && string.IsNullOrEmpty(uri.UserInfo);

    public static bool Configured(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.StartsWith("set-automatically", StringComparison.Ordinal);
}
