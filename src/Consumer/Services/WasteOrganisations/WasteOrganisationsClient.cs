using System.Net;
using System.Net.Http.Json;
using Defra.WasteObligations.Consumer.Services.Http;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Services.WasteOrganisations;

public sealed class WasteOrganisationsClient(HttpClient client, IOptions<WasteOrganisationsOptions> options)
    : IWasteOrganisationsClient
{
    public async Task<Organisation?> Read(Guid organisationId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest($"organisations/{organisationId:D}");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<Organisation>(cancellationToken);
    }

    public async Task CheckHealth(CancellationToken cancellationToken)
    {
        using var request = CreateRequest("health/authorized");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage CreateRequest(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path)
        {
            Version = client.DefaultRequestVersion,
            VersionPolicy = client.DefaultVersionPolicy,
        };
        request.Headers.Authorization = options.Value.CreateAuthorizationHeader();

        return request;
    }
}
