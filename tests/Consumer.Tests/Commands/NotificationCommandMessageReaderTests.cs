using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;

namespace Defra.WasteObligations.Consumer.Tests.Commands;

public sealed class NotificationCommandMessageReaderTests
{
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
