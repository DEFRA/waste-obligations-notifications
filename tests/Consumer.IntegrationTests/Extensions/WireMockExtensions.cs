using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Extensions;

public static class WireMockExtensions
{
    public static object Exact(string path) =>
        new { Matchers = new[] { new { Name = "ExactMatcher", Pattern = path } } };

    public static async Task Stub(this HttpClient client, Guid id, object request, object body, HttpStatusCode status)
    {
        using var result = await client.PostAsJsonAsync(
            "/__admin/mappings",
            new
            {
                Guid = id,
                Request = request,
                Response = new
                {
                    StatusCode = (int)status,
                    BodyAsJson = body,
                    Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                },
            },
            TestContext.Current.CancellationToken
        );
        result.EnsureSuccessStatusCode();
    }

    public static async Task<JsonElement[]> FindRequests(this HttpClient client, string method, string path)
    {
        using var result = await client.PostAsJsonAsync(
            "/__admin/requests/find",
            new { Methods = new[] { method }, Path = Exact(path) },
            TestContext.Current.CancellationToken
        );
        result.EnsureSuccessStatusCode();
        var requests = await result.Content.ReadFromJsonAsync<JsonElement[]>(TestContext.Current.CancellationToken);

        return requests ?? [];
    }
}
