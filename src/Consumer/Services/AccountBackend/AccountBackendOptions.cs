using System.ComponentModel.DataAnnotations;
using Defra.WasteObligations.Consumer.Services.Http;
using Microsoft.Extensions.Http.Resilience;

namespace Defra.WasteObligations.Consumer.Services.AccountBackend;

public sealed class AccountBackendOptions : DownstreamOptions
{
    public const string SectionName = "AccountBackend";

    public AccountBackendOptions() => Retry.DisableForUnsafeHttpMethods();

    [Required]
    public string TokenEndpoint { get; init; } = "set-automatically-when-deployed";

    [Required]
    public string ClientId { get; init; } = "set-automatically-when-deployed";

    [Required]
    public string ClientSecret { get; init; } = "set-automatically-when-deployed";
    public string? Scope { get; init; }

    public bool HasReadAccess =>
        SecureAddress(BaseAddress) && SecureAddress(TokenEndpoint) && Configured(ClientId) && Configured(ClientSecret);
}
