using System.Text.Json;
using Defra.WasteObligations.Consumer.Cancellation;
using Defra.WasteObligations.Consumer.IntegrationTests.Helpers;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
using Microsoft.Extensions.Configuration;
using Notify.Client;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class NotifyCancellationPreviewTests
{
    [Fact]
    public Task NotSignedByCorrectPersonEnglish_ShouldRenderCancellation() =>
        Preview("NotSignedByCorrectPersonEn", false);

    [Fact]
    public Task NotSignedByCorrectPersonWelsh_ShouldRenderCancellation() => Preview("NotSignedByCorrectPersonCy", true);

    [Fact]
    public Task RecyclingObligationsChangedEnglish_ShouldRenderCancellation() =>
        Preview("RecyclingObligationsChangedEn", false);

    [Fact]
    public Task RecyclingObligationsChangedWelsh_ShouldRenderCancellation() =>
        Preview("RecyclingObligationsChangedCy", true);

    [Fact]
    public Task CanMeetRecyclingObligationsEnglish_ShouldRenderCancellation() =>
        Preview("CanMeetRecyclingObligationsEn", false);

    [Fact]
    public Task CanMeetRecyclingObligationsWelsh_ShouldRenderCancellation() =>
        Preview("CanMeetRecyclingObligationsCy", true);

    [Fact]
    public Task ProducerRequestedEnglish_ShouldRenderCancellation() => Preview("ProducerRequestedEn", false);

    [Fact]
    public Task ProducerRequestedWelsh_ShouldRenderCancellation() => Preview("ProducerRequestedCy", true);

    private static async Task Preview(string mapping, bool welsh)
    {
        var (configuration, key) = NotifyPreviewSettings.Load();
        var options =
            configuration.GetSection(CancellationOptions.SectionName).Get<CancellationOptions>()
            ?? new CancellationOptions();
        var template = mapping switch
        {
            "NotSignedByCorrectPersonEn" => options.NotSignedByCorrectPersonEn,
            "NotSignedByCorrectPersonCy" => options.NotSignedByCorrectPersonCy,
            "RecyclingObligationsChangedEn" => options.RecyclingObligationsChangedEn,
            "RecyclingObligationsChangedCy" => options.RecyclingObligationsChangedCy,
            "CanMeetRecyclingObligationsEn" => options.CanMeetRecyclingObligationsEn,
            "CanMeetRecyclingObligationsCy" => options.CanMeetRecyclingObligationsCy,
            "ProducerRequestedEn" => options.ProducerRequestedEn,
            "ProducerRequestedCy" => options.ProducerRequestedCy,
            _ => throw new InvalidOperationException("Unknown cancellation template mapping."),
        };
        var declaration = new Declaration(
            "synthetic",
            1,
            2025,
            new DeclarationOrganisation(
                "synthetic",
                "GB-ENG",
                "DirectProducer",
                welsh ? "NRW" : "EA",
                "regulator@email.com"
            ),
            []
        );
        var content = CancellationPresentation.Create(
            declaration,
            welsh,
            new CancellationRecipient("synthetic@example.test", "SyntheticFirst", "SyntheticLast")
        );
        var personalisation = content
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property =>
                    property.Value.ValueKind == JsonValueKind.Number
                        ? (object)property.Value.GetInt32()
                        : property.Value.GetString()!
            );
        string body;
        try
        {
            var client = new NotificationClient(key);
            var preview = await client.GenerateTemplatePreviewAsync(template, personalisation);
            body = preview.body;
        }
        catch (Exception)
        {
            Assert.Fail(
                "Notify cancellation preview failed; check credential, template access and provider availability."
            );

            return;
        }

        // Boolean assertions prevent rendered content and provider responses entering reports.
        foreach (
            var value in new[]
            {
                "2026",
                welsh ? "Cyfoeth Naturiol Cymru" : "The Environment Agency",
                "regulator@email.com",
                welsh ? "tystysgrif" : "certificate",
                "SyntheticFirst",
                "SyntheticLast",
            }
        )
            Assert.True(
                body.Contains(value, StringComparison.Ordinal),
                "Cancellation preview body is missing required personalisation."
            );
    }
}
