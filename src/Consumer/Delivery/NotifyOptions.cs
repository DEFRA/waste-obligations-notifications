using System.ComponentModel.DataAnnotations;

namespace Defra.WasteObligations.Consumer.Delivery;

public sealed record NotifyOptions
{
    public const string SectionName = "Notify";

    [Required]
    public string ApiKey { get; init; } = "set-automatically-when-deployed";

    [Required]
    public string BaseAddress { get; init; } = "https://api.notifications.service.gov.uk";

    public bool HasValidApiKey =>
        !string.IsNullOrEmpty(ApiKey)
        && ApiKey.Length >= 73
        && Guid.TryParse(ApiKey.AsSpan(ApiKey.Length - 73, 36), out _)
        && ApiKey[^37] == '-'
        && Guid.TryParse(ApiKey.AsSpan(ApiKey.Length - 36), out _);
}
