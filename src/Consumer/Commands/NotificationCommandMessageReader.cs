using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Amazon.SQS.Model;

namespace Defra.WasteObligations.Consumer.Commands;

public static class NotificationCommandMessageReader
{
    private const string ContentEncodingHeader = "Content-Encoding";
    private const string GzipBase64ContentEncoding = "gzip+base64";
    private static readonly JsonSerializerOptions s_jsonSerializerOptions = new(JsonSerializerDefaults.Web);

    public static NotificationCommand Read(Message message)
    {
        var command =
            JsonSerializer.Deserialize<NotificationCommand>(ReadBody(message), s_jsonSerializerOptions)
            ?? throw new InvalidDataException("Notification command body is empty.");

        command.Validate();

        return command;
    }

    private static string ReadBody(Message message)
    {
        if (
            message.MessageAttributes is null
            || !message.MessageAttributes.TryGetValue(ContentEncodingHeader, out var contentEncoding)
            || contentEncoding.StringValue is null
        )
        {
            return message.Body;
        }

        if (contentEncoding.StringValue != GzipBase64ContentEncoding)
        {
            throw new InvalidOperationException("Notification command message content encoding is not supported.");
        }

        var bytes = Convert.FromBase64String(message.Body);
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        return reader.ReadToEnd();
    }
}
