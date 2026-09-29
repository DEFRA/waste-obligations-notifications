using System.Text.Json;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Tests.Delivery;

public sealed class NotificationCommandDigestTests
{
    [Fact]
    public void WhenObjectPropertiesAreReordered_ShouldPreserveImmutableDigest()
    {
        var digest = CreateDigest();
        var first = Command("{\"z\":[1,true,null,{\"b\":2,\"a\":1}],\"a\":\"value\"}");
        var second = Command("{\"a\":\"value\",\"z\":[1,true,null,{\"a\":1,\"b\":2}]}");

        Assert.Equal(digest.CreateImmutableFieldsDigest(first), digest.CreateImmutableFieldsDigest(second));
        Assert.Equal(
            digest.CreateImmutableFieldsDigest(first),
            digest.CreateImmutableFieldsDigest(first with { EmailAddress = " Recipient@EXAMPLE.com " })
        );
        Assert.NotEqual(
            digest.CreateImmutableFieldsDigest(first),
            digest.CreateImmutableFieldsDigest(first with { TemplateId = "different" })
        );
        Assert.NotEqual(
            digest.CreateImmutableFieldsDigest(first),
            digest.CreateImmutableFieldsDigest(Command("{\"z\":[true,1,null],\"a\":\"value\"}"))
        );
    }

    [Fact]
    public void WhenDigestPurposesDiffer_ShouldKeepEvidenceAndRecipientLanesSeparate()
    {
        var digest = CreateDigest();
        const string recipient = "recipient@example.com";
        var evidence = digest.CreateRecipientDigest(recipient);

        Assert.StartsWith("v1:", evidence);
        Assert.Equal(67, evidence.Length);
        Assert.Equal(evidence, digest.CreateRecipientDigest(" Recipient@EXAMPLE.com "));
        Assert.Equal(digest.CreateRecipientLane(recipient), digest.CreateRecipientLane(" Recipient@EXAMPLE.com "));
        Assert.NotEqual(evidence, digest.CreateRecipientLane(recipient));
        Assert.NotEqual(evidence, digest.CreateIdempotencyKeyDigest(recipient));
        Assert.NotEqual(evidence, CreateDigest("another-secret").CreateRecipientDigest(recipient));
        Assert.DoesNotContain(recipient, evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenSecretIsPlaceholder_ShouldRejectDigestCreation()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CreateDigest("set-automatically-by-deployment").CreateIdempotencyKeyDigest("key")
        );
    }

    private static NotificationCommandDigest CreateDigest(string secret = "test-evidence-secret") =>
        new(
            Options.Create(
                new NotificationCommandDeliveryOptions
                {
                    QueueUrl = "local",
                    EmailDeliveryCutoverUtc = "2026-09-29T00:00:00Z",
                    EvidenceDigestSecret = secret,
                    RecipientLaneSecret = "test-lane-secret",
                }
            )
        );

    private static NotificationCommand Command(string personalisation) =>
        new(
            1,
            "key",
            DateTimeOffset.Parse("2026-09-28T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            "submitted",
            "recipient@example.com",
            "template",
            JsonDocument.Parse(personalisation).RootElement.Clone()
        );
}
