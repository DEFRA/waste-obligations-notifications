using Defra.WasteObligations.Consumer.Cancellation;
using Defra.WasteObligations.Consumer.Services.WasteObligations;

namespace Defra.WasteObligations.Consumer.Tests.Cancellation;

public sealed class CancellationPresentationTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(" Unfamiliar Authority ", "Unfamiliar Authority")]
    [InlineData(" EA ", "The Environment Agency")]
    [InlineData("The Environment Agency", "The Environment Agency")]
    [InlineData("SEPA", "The Scottish Environment Protection Agency")]
    [InlineData("The Scottish Environment Protection Agency", "The Scottish Environment Protection Agency")]
    [InlineData("NIEA", "The Northern Ireland Environment Agency")]
    [InlineData("The Northern Ireland Environment Agency", "The Northern Ireland Environment Agency")]
    [InlineData("NRW", "Natural Resources Wales")]
    [InlineData("Natural Resources Wales", "Natural Resources Wales")]
    public void WhenRegulatorIsPresented_ShouldUseLegacyNamesAndPreserveOptionalValues(
        string? regulator,
        string? expected
    )
    {
        var declaration = new Declaration(
            "id",
            1,
            2026,
            new DeclarationOrganisation("org", "GB-WLS", "DirectProducer", regulator, null),
            []
        );

        var result = CancellationPresentation.Create(
            declaration,
            false,
            new CancellationRecipient("recipient@example.test", "First", "Last")
        );

        Assert.Equal(expected, result.GetProperty("regulator").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("regulatorEmail").ValueKind);
        Assert.False(result.TryGetProperty("regulator_cy", out _));
    }

    [Fact]
    public void WhenWelshRegulatorIsNotNaturalResourcesWales_ShouldOmitWelshRegulatorName()
    {
        var declaration = new Declaration(
            "id",
            1,
            2026,
            new DeclarationOrganisation("org", "GB-WLS", "DirectProducer", "EA", "regulator@example.test"),
            []
        );

        var result = CancellationPresentation.Create(
            declaration,
            true,
            new CancellationRecipient("recipient@example.test", "First", "Last")
        );

        Assert.Equal("The Environment Agency", result.GetProperty("regulator").GetString());
        Assert.False(result.TryGetProperty("regulator_cy", out _));
    }

    [Theory]
    [InlineData(
        "DirectProducer",
        "certificate",
        "tystysgrif",
        "acquire enough packaging waste recycling notes (PRNs) or packaging waste export recycling notes (PERNs) to meet your recycling obligations",
        "caffael digon o nodiadau ailgylchu gwastraff pecynwaith (PRNs) neu nodiadau ailgylchu allforio gwastraff pecynwaith (PERNs) i gyflawni eich rhwymedigaethau ailgylchu bob blwyddyn",
        "acquire enough packaging waste recycling notes (PRNs) or packaging waste export recycling notes (PERNs) to meet your revised recycling obligations",
        "caffael digon o nodiadau ailgylchu gwastraff pecynwaith (PRNs) neu nodiadau ailgylchu allforio gwastraff pecynwaith (PERNs) i fodloni eich rhwymedigaethau ailgylchu diwygiedig"
    )]
    [InlineData(
        "ComplianceScheme",
        "statement",
        "datganiad",
        "meet your recycling obligations",
        "cyflawni eich rhwymedigaethau ailgylchu",
        "meet your revised recycling obligations",
        "cyflawni eich rhwymedigaethau ailgylchu diwygiedig"
    )]
    public void WhenRegistrationDeterminesDocuments_ShouldIncludeBothLanguages(
        string registration,
        string document,
        string welshDocument,
        string bullet,
        string welshBullet,
        string revisedBullet,
        string welshRevisedBullet
    )
    {
        var declaration = new Declaration(
            "id",
            1,
            2025,
            new DeclarationOrganisation("org", "GB-ENG", registration, "EA", "regulator@example.test"),
            []
        );

        var result = CancellationPresentation.Create(
            declaration,
            true,
            new CancellationRecipient("recipient@example.test", "First", "Last")
        );

        Assert.Equal(2026, result.GetProperty("year").GetInt32());
        Assert.Equal(document, result.GetProperty("certOrStatement").GetString());
        Assert.Equal(welshDocument, result.GetProperty("certOrStatement_cy").GetString());
        Assert.Equal(bullet, result.GetProperty("certOrStatementBullet").GetString());
        Assert.Equal(welshBullet, result.GetProperty("certOrStatementBullet_cy").GetString());
        Assert.Equal(revisedBullet, result.GetProperty("certOrStatementBullet2").GetString());
        Assert.Equal(welshRevisedBullet, result.GetProperty("certOrStatementBullet2_cy").GetString());
    }
}
