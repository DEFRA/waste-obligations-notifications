using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Defra.WasteObligations.Consumer.Commands;
using Microsoft.Extensions.Options;
using Notify.Authentication;

namespace Defra.WasteObligations.Consumer.Delivery;

public sealed class NotifyEmailClient(HttpClient httpClient, IOptions<NotifyOptions> options) : INotifyEmailClient
{
    public async Task CheckHealth(CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateAuthenticatedRequest(HttpMethod.Get, "/v2/templates?type=email");
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
            cancellationToken.ThrowIfCancellationRequested();
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("Notify health request did not succeed.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Notify health request failed.");
        }
    }

    public async Task<NotifyAcceptance> Send(
        NotificationCommand command,
        string reference,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var request = CreateAuthenticatedRequest(HttpMethod.Post, "/v2/notifications/email");
            request.Content = JsonContent.Create(
                new
                {
                    email_address = command.EmailAddress.Trim().ToLowerInvariant(),
                    template_id = command.TemplateId,
                    personalisation = command.Personalisation,
                    reference,
                }
            );
            // ResponseContentRead and the same token bound the complete request, including response buffering.
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken
            );
            if (response.StatusCode != HttpStatusCode.Created)
                throw new InvalidOperationException("Notify did not accept the email request.");

            using var body = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken
            );
            var root = body.RootElement;
            var id = root.GetProperty("id").GetString();
            var returnedReference = root.GetProperty("reference").GetString();
            var template = root.GetProperty("template");
            var templateId = template.GetProperty("id").GetString();
            var version = template.GetProperty("version").GetInt32();
            var acceptance = new NotifyAcceptance(id ?? "", returnedReference ?? "", templateId ?? "", version);
            if (!acceptance.Matches(command, reference))
                throw new InvalidDataException("Notify acceptance evidence is incomplete or inconsistent.");

            return acceptance;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // HTTP and SDK exception text can contain the recipient, request or response. Keep it outside logs.
            throw new InvalidOperationException("Notify email request failed or returned invalid acceptance evidence.");
        }
    }

    private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string path)
    {
        var key = options.Value.ApiKey;
        if (!options.Value.HasValidApiKey)
            throw new InvalidOperationException("Notify ApiKey has not been configured.");
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            Authenticator.CreateToken(key[^36..], key.Substring(key.Length - 73, 36))
        );

        return request;
    }
}
