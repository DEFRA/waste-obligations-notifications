using System.ComponentModel.DataAnnotations;

namespace Defra.WasteObligations.Consumer.Submission;

public sealed record SubmissionEmailOptions
{
    public const string SectionName = "SubmissionEmail";

    public string ApiBaseAddress { get; init; } = "set-automatically-when-deployed";

    public string ClientId { get; init; } = "set-automatically-when-deployed";

    public string ClientSecret { get; init; } = "set-automatically-when-deployed";

    [Required]
    public string DirectEnglishTemplateId { get; init; } = "5f64e3bd-d454-4a45-a9c6-9409bf940d7a";

    [Required]
    public string DirectWelshTemplateId { get; init; } = "b3223b0b-a467-40c1-9150-f78b76d11fd8";

    [Required]
    public string SchemeEnglishTemplateId { get; init; } = "b103685d-de8a-4ea9-abc8-d244ca26841b";

    [Required]
    public string SchemeWelshTemplateId { get; init; } = "95d8c2aa-a229-4ecb-9774-78ec06d0cbac";

    public bool HasValidTemplates =>
        Guid.TryParseExact(DirectEnglishTemplateId, "D", out _)
        && Guid.TryParseExact(DirectWelshTemplateId, "D", out _)
        && Guid.TryParseExact(SchemeEnglishTemplateId, "D", out _)
        && Guid.TryParseExact(SchemeWelshTemplateId, "D", out _);

    public bool HasReadConfiguration =>
        Uri.TryCreate(ApiBaseAddress, UriKind.Absolute, out var address)
        && address.Scheme is "http" or "https"
        && string.IsNullOrEmpty(address.UserInfo)
        && string.IsNullOrEmpty(address.Query)
        && string.IsNullOrEmpty(address.Fragment)
        && IsConfigured(ClientId)
        && !ClientId.Contains(':')
        && IsConfigured(ClientSecret);

    private static bool IsConfigured(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.StartsWith("set-automatically", StringComparison.Ordinal)
        && !value.Any(char.IsControl);
}
