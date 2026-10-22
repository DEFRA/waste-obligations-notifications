using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Http.Resilience;

namespace Defra.WasteObligations.Consumer.Services.Http;

public abstract class BasicAuthenticationOptions : DownstreamOptions
{
    protected BasicAuthenticationOptions() => Retry.DisableForUnsafeHttpMethods();

    [Required]
    public string Username { get; init; } = "set-automatically-when-deployed";

    [Required]
    public string Password { get; init; } = "set-automatically-when-deployed";

    public bool HasValidCredentials => Configured(Username) && Configured(Password) && !Username.Contains(':');
}
