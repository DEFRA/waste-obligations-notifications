using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Submission;

public sealed class DeclarationReadClient(HttpClient client, IOptions<SubmissionOptions> options) : IDeclarationReadClient
{
    public async Task<JsonElement> Read(Guid organisationId, string declarationId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest($"organisations/{organisationId:D}/compliance-declarations/{declarationId}");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new SubmissionProcessingException("declaration-not-found");
        response.EnsureSuccessStatusCode();
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new SubmissionProcessingException("invalid-declaration-data");
        }
    }

    public async Task CheckHealth(CancellationToken cancellationToken)
    {
        using var request = CreateRequest("health");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage CreateRequest(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Value.ClientId}:{options.Value.ClientSecret}")));

        return request;
    }
}
