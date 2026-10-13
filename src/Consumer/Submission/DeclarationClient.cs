using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Submission;

public sealed class DeclarationClient(HttpClient client, IOptions<SubmissionEmailOptions> options) : IDeclarationClient
{
    public async Task<JsonDocument> Read(SubmissionEvent submission, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"organisations/{submission.OrganisationId:D}/compliance-declarations/{submission.DeclarationId}"
        );
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(options.Value.ClientId + ":" + options.Value.ClientSecret))
        );
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new SubmissionProcessingException("declaration-not-found");
        if (!response.IsSuccessStatusCode)
            throw new SubmissionProcessingException("declaration-read-failed");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        return JsonDocument.Parse(bytes);
    }

    public async Task<bool> CheckHealth(CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            "health",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );

        return response.IsSuccessStatusCode;
    }
}
