using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Defra.WasteObligations.Consumer.Tests.Helpers;

public static class HttpResponseSnapshotExtensions
{
    public static async Task VerifyResponse(
        this HttpResponseMessage response,
        string? variant = null,
        bool scrubVerificationIdentity = false,
        [CallerFilePath] string sourceFile = "",
        [CallerMemberName] string sourceMember = ""
    )
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        ScrubOpaqueValues(body);
        if (scrubVerificationIdentity && body is JsonObject command && command["idempotencyKey"] is JsonValue)
            command["idempotencyKey"] = "verification-<generated>";
        var snapshot = new JsonObject
        {
            ["statusCode"] = (int)response.StatusCode,
            ["contentType"] = response.Content.Headers.ContentType?.ToString(),
            ["location"] = response.Headers.Location?.OriginalString,
            ["body"] = body,
        };

        await Verifier
            .VerifyJson(snapshot.ToJsonString(), sourceFile: sourceFile)
            .UseTypeName(Path.GetFileNameWithoutExtension(sourceFile))
            .UseMethodName(variant is null ? sourceMember : $"{sourceMember}_{variant}")
            .DontScrubDateTimes()
            .DontScrubGuids();
    }

    private static void ScrubOpaqueValues(JsonNode? node)
    {
        if (node is JsonObject value)
        {
            foreach (var property in value.ToArray())
            {
                if (property.Key == "selectionToken" && property.Value is JsonValue)
                    value[property.Key] = "<opaque-selection-token>";
                else if (property.Key == "traceId" && property.Value is JsonValue)
                    value[property.Key] = "<request-trace-id>";
                else
                    ScrubOpaqueValues(property.Value);
            }
        }
        else if (node is JsonArray items)
        {
            foreach (var item in items)
                ScrubOpaqueValues(item);
        }
    }
}
