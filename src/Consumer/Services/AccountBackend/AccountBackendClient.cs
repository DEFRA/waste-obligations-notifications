using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Defra.WasteObligations.Consumer.Services.AccountBackend;

public sealed class AccountBackendClient(HttpClient client, AccountTokenProvider tokens) : IAccountBackendClient
{
    public async Task<IReadOnlyList<AccountOrganisation>> SearchOrganisationsByCompaniesHouseNumbers(
        IReadOnlyCollection<string> companiesHouseNumbers,
        CancellationToken cancellationToken
    )
    {
        using var request = await CreateRequest(
            HttpMethod.Post,
            "api/organisations/organisations-by-companies-house-numbers",
            cancellationToken
        );
        request.Content = JsonContent.Create(new CompaniesHouseSearch(companiesHouseNumbers));
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<IReadOnlyList<AccountOrganisation>>(cancellationToken)
            ?? throw new InvalidOperationException("Account organisation search response unavailable");
    }

    public async Task<OrganisationWithPersons?> ReadOrganisationWithPersons(
        Guid organisationId,
        CancellationToken cancellationToken
    )
    {
        using var request = await CreateRequest(
            HttpMethod.Get,
            $"api/organisations/organisation-with-persons/{organisationId:D}",
            cancellationToken
        );
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<OrganisationWithPersons>(cancellationToken);
    }

    public async Task CheckHealth(CancellationToken cancellationToken)
    {
        using var request = await CreateRequest(HttpMethod.Get, "admin/health", cancellationToken);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpRequestMessage> CreateRequest(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken
    )
    {
        var token = await tokens.GetToken(cancellationToken);
        var request = new HttpRequestMessage(method, path)
        {
            Version = client.DefaultRequestVersion,
            VersionPolicy = client.DefaultVersionPolicy,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return request;
    }
}
