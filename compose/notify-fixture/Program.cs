using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
    if (File.Exists(keyPath))
    {
        const string prefix = "synthetic-fixture-";
        var existing = File.ReadAllText(keyPath);
        if (
            !existing.StartsWith(prefix, StringComparison.Ordinal)
            || existing.Length != prefix.Length + 73
            || existing[prefix.Length + 36] != '-'
            || !Guid.TryParseExact(existing.Substring(prefix.Length, 36), "D", out var existingServiceId)
            || !Guid.TryParseExact(existing[(prefix.Length + 37)..], "D", out var existingSecretId)
        )
            throw new InvalidOperationException("Local Notify fixture credentials are invalid.");
        serviceId = existingServiceId.ToString();
        secretId = existingSecretId.ToString();
        apiKey = existing;
    }
    else
    {
        File.WriteAllText($"{keyPath}.tmp", apiKey);
        File.Move($"{keyPath}.tmp", keyPath, true);
    }
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
app.MapGet(
    "/v2/templates",
    (HttpRequest request) =>
    {
        if (!IsAuthenticated(request))
            return Results.Unauthorized();
        if (request.Query["type"] != "email")
            return Results.BadRequest();

        return Results.Ok(new { templates = Array.Empty<object>() });
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

bool IsAuthenticated(HttpRequest request)
{
    var authorization = request.Headers.Authorization.ToString();
    if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        return false;
    var token = authorization[7..].Split('.');
    if (token.Length != 3)
        return false;
    try
    {
        using var header = JsonDocument.Parse(DecodeBase64Url(token[0]));
        using var payload = JsonDocument.Parse(DecodeBase64Url(token[1]));
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secretId),
            Encoding.ASCII.GetBytes($"{token[0]}.{token[1]}")
        );

        return header.RootElement.GetProperty("alg").GetString() == "HS256"
            && payload.RootElement.GetProperty("iss").GetString() == serviceId
            && CryptographicOperations.FixedTimeEquals(signature, DecodeBase64Url(token[2]));
    }
    catch (Exception)
    {
        return false;
    }
}

static byte[] DecodeBase64Url(string value)
{
    var base64 = value.Replace('-', '+').Replace('_', '/');

    return Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
}
