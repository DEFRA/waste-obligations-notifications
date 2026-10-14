using System.ComponentModel.DataAnnotations;

namespace Defra.WasteObligations.Consumer.Submission;

public sealed record SubmissionOptions
{
    public const string SectionName = "Submission";

    public string BaseAddress { get; init; } = "set-automatically-when-deployed";
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
        new[] { DirectEnglishTemplateId, DirectWelshTemplateId, SchemeEnglishTemplateId, SchemeWelshTemplateId }.All(
            value => Guid.TryParse(value, out var id) && id != Guid.Empty
        );

    public bool HasReadAccess =>
        Uri.TryCreate(BaseAddress, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && Configured(ClientId)
        && Configured(ClientSecret)
        && !ClientId.Contains(':');

    private static bool Configured(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.StartsWith("set-automatically", StringComparison.Ordinal);
}
