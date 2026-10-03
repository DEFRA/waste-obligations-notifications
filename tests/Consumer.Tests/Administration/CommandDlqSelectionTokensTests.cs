using System.Text;
using System.Text.Json;
using Defra.WasteObligations.Consumer.Administration;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Tests.Administration;

public sealed class CommandDlqSelectionTokensTests
{
    private const string Evidence = "v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Secret = "shared-private-evidence-secret";
    private const string QueueUrl = "http://sqs.local/commands-dlq.fifo";

    [Theory]
    [InlineData("opaque-message-id")]
    [InlineData("escaped")]
    public void WhenCorrectlyConfiguredHostsHaveDifferentSelectionLifetimes_ShouldVerifySameContentFreeSelection(
        string id
    )
    {
        var clock = new ControlledTimeProvider();
        var first = Create(clock, 120);
        var second = Create(clock, 60);
        var messageId = id == "escaped" ? new string('ü', 100) : id;
        var receiveId = "11111111-1111-1111-1111-111111111111";
        var expires = clock.GetUtcNow().AddSeconds(120);

        var token = first.Create(receiveId, messageId, expires, Evidence);
        var selection = second.Validate(token);

        Assert.NotNull(selection);
        Assert.Equal(receiveId, selection.ReceiveRequestAttemptId);
        Assert.Equal(messageId, selection.MessageId);
        Assert.Equal(expires, selection.ExpiresAtUtc);
        Assert.Equal(Evidence, selection.ImmutableFieldsDigest);
        Assert.Equal(120, selection.VisibilityTimeoutSeconds);
        Assert.StartsWith("v2.", token);
        Assert.StartsWith("v1:", selection.QueueBinding);
        var encoded = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var payload = Encoding.UTF8.GetString(
            Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='))
        );
        using var body = JsonDocument.Parse(payload);
        Assert.Equal(
            [
                "expiresAtUtc",
                "immutableFieldsDigest",
                "messageId",
                "queueBinding",
                "receiveRequestAttemptId",
                "visibilityTimeoutSeconds",
            ],
            body.RootElement.EnumerateObject().Select(property => property.Name).Order()
        );
        Assert.DoesNotContain(Secret, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(QueueUrl, payload, StringComparison.Ordinal);
        Assert.True(token.Length <= 4096);
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("queue")]
    [InlineData("expired")]
    [InlineData("tampered")]
    [InlineData("malformed")]
    [InlineData("legacy-version")]
    public void WhenSelectionTrustOrValidityChanges_ShouldRejectIt(string condition)
    {
        var clock = new ControlledTimeProvider();
        var issuer = Create(clock, 120);
        var token = issuer.Create(
            "11111111-1111-1111-1111-111111111111",
            "opaque-message-id",
            clock.GetUtcNow().AddSeconds(120),
            Evidence
        );
        var receiver = Create(
            clock,
            60,
            condition == "secret" ? "other-private-secret" : Secret,
            condition == "queue" ? "http://sqs.local/other-dlq.fifo" : QueueUrl
        );
        if (condition == "expired")
            clock.Now = clock.Now.AddSeconds(120);
        if (condition == "tampered")
        {
            var parts = token.Split('.');
            parts[2] = (parts[2][0] == 'A' ? 'B' : 'A') + parts[2][1..];
            token = string.Join('.', parts);
        }
        if (condition == "malformed")
            token = "v1.private-invalid-payload.invalid-signature";

        if (condition == "legacy-version")
            token = "v1" + token[2..];

        Assert.Null(receiver.Validate(token));
    }

    [Fact]
    public void WhenVerifierClockIsSlightlyEarlier_ShouldAcceptTrustedUnexpiredSelectionNearAwsWindow()
    {
        var issuerClock = new ControlledTimeProvider();
        var issuer = Create(issuerClock, 299);
        var receiverClock = new ControlledTimeProvider { Now = issuerClock.Now.AddSeconds(-5) };
        var receiver = Create(receiverClock, 60);
        var expires = issuerClock.Now.AddSeconds(299);

        var token = issuer.Create("11111111-1111-1111-1111-111111111111", "opaque-message-id", expires, Evidence);
        var selection = receiver.Validate(token);

        Assert.NotNull(selection);
        Assert.Equal(expires, selection.ExpiresAtUtc);
    }

    private static CommandDlqSelectionTokens Create(
        TimeProvider clock,
        int lifetime,
        string secret = Secret,
        string queue = QueueUrl
    ) =>
        new(
            Options.Create(
                new CommandDlqAdministrationOptions { QueueUrl = queue, SelectionLifetimeSeconds = lifetime }
            ),
            Options.Create(
                new NotificationCommandDeliveryOptions
                {
                    QueueUrl = "http://sqs.local/commands.fifo",
                    EmailDeliveryCutoverUtc = "set-automatically-when-deployed",
                    EvidenceDigestSecret = secret,
                    RecipientLaneSecret = "private-recipient-secret",
                }
            ),
            clock
        );

    private sealed class ControlledTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
