using System.Collections.Concurrent;
using System.Text.Json;

if (args.Contains("--health-check", StringComparer.Ordinal))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
    try
    {
        using var response = await client.GetAsync("http://localhost:8080/health");
        Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception)
    {
        Environment.ExitCode = 1;
    }

    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
var app = builder.Build();
var requests = new ConcurrentQueue<JsonElement>();

// These random credentials belong only to the isolated local fixture, never to a Notify account.
var serviceId = Guid.NewGuid().ToString();
var secretId = Guid.NewGuid().ToString();
var apiKey = $"synthetic-fixture-{serviceId}-{secretId}";
var keyPath = Environment.GetEnvironmentVariable("FIXTURE_KEY_PATH");
if (keyPath is not null)
{
    File.WriteAllText($"{keyPath}.tmp", apiKey);
    File.Move($"{keyPath}.tmp", keyPath, true);
}
if (keyPath is not null)
{
    var bootstrapPath = Path.Combine(Path.GetDirectoryName(keyPath)!, "start-consumer.sh");
    File.Copy(Path.Combine(AppContext.BaseDirectory, "start-consumer.sh"), $"{bootstrapPath}.tmp", true);
    File.Move($"{bootstrapPath}.tmp", bootstrapPath, true);
}
app.MapGet("/test/api-key", () => Results.Text(apiKey));
app.MapGet("/health", () => Results.Ok());
app.MapGet("/test/requests", () => requests.ToArray());
app.MapDelete(
    "/test/requests",
    () =>
    {
        requests.Clear();
        return Results.NoContent();
    }
);
app.MapPost(
    "/v2/notifications/email",
    async (HttpContext context) =>
    {
        using var document = await JsonDocument.ParseAsync(
            context.Request.Body,
            cancellationToken: context.RequestAborted
        );
        var body = document.RootElement.Clone();
        requests.Enqueue(body);
        var templateId = body.GetProperty("template_id").GetString();
        if (templateId == "failed-template")
            return Results.Json(new { errors = "controlled failure" }, statusCode: 400);
        if (templateId == "slow-template")
            await Task.Delay(TimeSpan.FromSeconds(3), context.RequestAborted);

        return Results.Json(
            new
            {
                id = Guid.NewGuid().ToString(),
                reference = body.GetProperty("reference").GetString(),
                template = new { id = templateId, version = 1 },
                content = new { body = "controlled rendered content", subject = "controlled subject" },
            },
            statusCode: 201
        );
    }
);
app.Run();
