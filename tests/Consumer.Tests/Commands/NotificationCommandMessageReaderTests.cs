using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;

namespace Defra.WasteObligations.Consumer.Tests.Commands;

public sealed class NotificationCommandMessageReaderTests
{
    [Fact]
    public void WhenSourceIsPresent_ShouldRetainValidSourceAndIgnoreEmailIdentifiers()
    {
        var message = CreateMessage("\"2026-10-01T00:00:00Z\"");
        message.Body = message.Body.Replace(
            "\"personalisation\": {}",
            "\"personalisation\": {}, \"source\": {\"type\": \"future-event\", \"id\": \"entity-123\"}",
            StringComparison.Ordinal
        );
        var command = NotificationCommandMessageReader.Read(message);
        Assert.Equal("future-event", command.Source?.Type);
        Assert.Equal("entity-123", command.Source?.Id);
        message.Body = message.Body.Replace("entity-123", "person@example.invalid", StringComparison.Ordinal);
        Assert.Null(NotificationCommandMessageReader.Read(message).Source);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"event\",\"id\":42}")]
    public void WhenSourceIsMalformed_ShouldIgnoreItWithoutRejectingCommand(string sourceJson)
    {
        var message = CreateMessage("\"2026-10-01T00:00:00Z\"");
        message.Body = message.Body.Replace(
            "\"personalisation\": {}",
            "\"personalisation\": {}, \"source\": " + sourceJson,
            StringComparison.Ordinal
        );
        Assert.Null(NotificationCommandMessageReader.Read(message).Source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void WhenResendParentIsOptionalOpaqueKey_ShouldPreserveMetadata(string? parent)
    {
        var message = CreateMessage("\"2026-10-01T00:00:00Z\"");
        message.Body = message.Body.Replace(
            "\"personalisation\": {}",
            $"\"personalisation\": {{}}, \"resendOfNotificationKey\": {JsonSerializer.Serialize(parent)}",
            StringComparison.Ordinal
        );
        if (parent is not null)
        {
            message.Body = message.Body.Replace("key-1", "resend:" + parent, StringComparison.Ordinal);
        }

        var command = NotificationCommandMessageReader.Read(message);

        Assert.Equal(parent, command.ResendOfNotificationKey);
    }

    [Theory]
    [InlineData("key-1", true)]
    [InlineData("resend:v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("resend:v1:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", true)]
    [InlineData("Resend:v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("resend:", false)]
    public void WhenResendIdentityDoesNotMatchParent_ShouldRejectWithoutExposingValues(
        string idempotencyKey,
        bool hasParent
    )
    {
        const string parent = "v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var message = CreateMessage("\"2026-10-01T00:00:00Z\"");
        message.Body = message.Body.Replace("key-1", idempotencyKey, StringComparison.Ordinal);
        if (hasParent)
        {
            message.Body = message.Body.Replace(
                "\"personalisation\": {}",
                $"\"personalisation\": {{}}, \"resendOfNotificationKey\": {JsonSerializer.Serialize(parent)}",
                StringComparison.Ordinal
            );
        }

        var exception = Assert.Throws<InvalidDataException>(() => NotificationCommandMessageReader.Read(message));

        Assert.Equal("Notification command resend metadata is inconsistent.", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("private-parent@example.com")]
    [InlineData("v2:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("v1:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    public void WhenResendParentIsMalformed_ShouldRejectWithoutExposingValue(string parent)
    {
        var message = CreateMessage("\"2026-10-01T00:00:00Z\"");
        message.Body = message.Body.Replace(
            "\"personalisation\": {}",
            $"\"personalisation\": {{}}, \"resendOfNotificationKey\": {JsonSerializer.Serialize(parent)}",
            StringComparison.Ordinal
        );

        var exception = Assert.Throws<InvalidDataException>(() => NotificationCommandMessageReader.Read(message));

        Assert.Equal("Notification command resendOfNotificationKey must be a valid opaque key.", exception.Message);
    }

    [Theory]
    [InlineData("2026-10-01T00:00:00.1234567Z", false)]
    [InlineData("2026-10-01T00:00:00.1234567+00:00", false)]
    [InlineData("2026-10-01T00:00:00.1234567-00:00", false)]
    [InlineData("2026-10-01T00:00:00.1234567Z", true)]
    [InlineData("2026-10-01T00:00:00.12345676Z", false)]
    [InlineData("2026-10-01T00:00:00.12399996Z", false)]
    [InlineData("2026-10-01T00:00:00.12399996Z", true)]
    public void WhenActionTimestampIsExplicitUtc_ShouldTruncateToMongoPrecision(string timestamp, bool compressed)
    {
        var command = NotificationCommandMessageReader.Read(
            CreateMessage(JsonSerializer.Serialize(timestamp), compressed)
        );

        Assert.Equal(
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(123),
            command.ActionOccurredAtUtc
        );
    }

    [Theory]
    [InlineData("2026-10-01T00:00:00", false)]
    [InlineData("2026-10-01T00:00:00", true)]
    [InlineData("2026-10-01T00:00:00+01:00", false)]
    [InlineData("2026-10-01T00:00:00-01:00", false)]
    [InlineData("private-invalid-timestampZ", false)]
    [InlineData("2026-02-30T00:00:00Z", false)]
    [InlineData("00:00Z", false)]
    public void WhenActionTimestampIsNotExplicitValidUtc_ShouldRejectWithoutExposingValue(
        string timestamp,
        bool compressed
    )
    {
        var exception = Assert.Throws<JsonException>(() =>
            NotificationCommandMessageReader.Read(CreateMessage(JsonSerializer.Serialize(timestamp), compressed))
        );

        Assert.DoesNotContain(timestamp, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("17")]
    [InlineData("{}")]
    public void WhenActionTimestampIsNotAString_ShouldReject(string timestampJson)
    {
        Assert.Throws<JsonException>(() => NotificationCommandMessageReader.Read(CreateMessage(timestampJson)));
    }

    internal static Message CreateMessage(string timestampJson, bool compressed = false)
    {
        var body = $$"""
            { "schemaVersion": 1, "idempotencyKey": "key-1", "actionOccurredAtUtc": {{timestampJson}},
              "notificationType": "submitted", "emailAddress": "recipient@example.com", "templateId": "template-1",
              "personalisation": {} }
            """;
        var message = new Message { Body = body, MessageAttributes = [] };
        if (compressed)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
                gzip.Write(Encoding.UTF8.GetBytes(body));
            message.Body = Convert.ToBase64String(output.ToArray());
            message.MessageAttributes["Content-Encoding"] = new() { DataType = "String", StringValue = "gzip+base64" };
        }

        return message;
    }
}
