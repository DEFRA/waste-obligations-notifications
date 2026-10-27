using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Defra.WasteObligations.Consumer.Services.Http;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Services.WasteObligations;

public sealed class WasteObligationsClient(HttpClient client, IOptions<WasteObligationsOptions> options)
    : IWasteObligationsClient
{
    public async Task<Declaration?> ReadDeclaration(
        Guid organisationId,
        string declarationId,
        CancellationToken cancellationToken
    )
    {
        using var request = CreateRequest($"organisations/{organisationId:D}/compliance-declarations/{declarationId}");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<Declaration>(cancellationToken)
            ?? throw new JsonException("Declaration response is missing.");
    }

    public async Task<UnsubmittedOrganisationsPage> SearchUnsubmittedOrganisations(
        int obligationYear,
        string registrationType,
        int page,
        int pageSize,
        CancellationToken cancellationToken
    )
    {
        var query =
            $"compliance-declarations/unsubmitted?obligationYear={obligationYear}&registrationType={Uri.EscapeDataString(registrationType)}&page={page}&pageSize={pageSize}";
        using var request = CreateRequest(query);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<UnsubmittedOrganisationsPage>(cancellationToken)
            ?? throw new JsonException("Unsubmitted organisations response is missing.");
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
            Version = client.BaseAddress?.Scheme == "https" ? HttpVersion.Version20 : HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.Authorization = options.Value.CreateAuthorizationHeader();

        return request;
    }
}
