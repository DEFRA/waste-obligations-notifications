using System.ComponentModel.DataAnnotations;

namespace Defra.WasteObligations.Consumer.Cancellation;

public sealed record CancellationOptions
{
    public const string SectionName = "Cancellation";

    [Required]
    public string NotSignedByCorrectPersonEn { get; init; } = "1502cfa2-9758-4410-b189-d1f95a7c774f";

    [Required]
    public string NotSignedByCorrectPersonCy { get; init; } = "7cab1c5e-9edf-4139-b3fd-3db4bcf3041d";

    [Required]
    public string RecyclingObligationsChangedEn { get; init; } = "857808d4-d159-421e-92b6-bf9d59711a9e";

    [Required]
    public string RecyclingObligationsChangedCy { get; init; } = "0a966633-b7ad-42dd-b286-af0cb2405ed2";

    [Required]
    public string CanMeetRecyclingObligationsEn { get; init; } = "86345150-cb25-4b59-94c3-578bed7e82d4";

    [Required]
    public string CanMeetRecyclingObligationsCy { get; init; } = "308e7224-0ab1-4f54-8e8d-1558c9c57a77";

    [Required]
    public string ProducerRequestedEn { get; init; } = "3e03c93f-955c-4db3-936c-bfa3f7725a5f";

    [Required]
    public string ProducerRequestedCy { get; init; } = "e419a544-e1b3-4ea5-b8eb-a074e63aea1a";

    public bool HasValidTemplates =>
        new[]
        {
            NotSignedByCorrectPersonEn,
            NotSignedByCorrectPersonCy,
            RecyclingObligationsChangedEn,
            RecyclingObligationsChangedCy,
            CanMeetRecyclingObligationsEn,
            CanMeetRecyclingObligationsCy,
            ProducerRequestedEn,
            ProducerRequestedCy,
        }.All(value => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty);
}
