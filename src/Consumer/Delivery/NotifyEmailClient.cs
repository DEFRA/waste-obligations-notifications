using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Defra.WasteObligations.Consumer.Commands;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Notify.Authentication;
using Notify.Interfaces;

namespace Defra.WasteObligations.Consumer.Delivery;

public sealed class NotifyEmailClient(
    HttpClient httpClient,
    IOptions<NotifyOptions> options,
    Func<IHttpClient, NotifyOptions, IAsyncNotificationClient> notificationClientFactory
) : INotifyEmailClient
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
            command.Validate();
            using var transport = new NotifySdkHttpClient(httpClient, cancellationToken, HttpStatusCode.Created);
            var client = notificationClientFactory(transport, options.Value);
            var personalisation = CreatePersonalisation(command.Personalisation);
            // The pinned SDK blocks before returning its task. Keep that off the caller while the adapter
            // carries the actual operation token through the HTTP request and complete body buffering.
            var response = await Task.Run(
                () =>
                    client.SendEmailAsync(
                        command.EmailAddress.Trim().ToLowerInvariant(),
                        command.TemplateId,
                        personalisation,
                        reference
                    ),
                cancellationToken
            );
            cancellationToken.ThrowIfCancellationRequested();
            var acceptance = new NotifyAcceptance(
                response?.id ?? "",
                response?.reference ?? "",
                response?.template?.id ?? "",
                response?.template?.version ?? 0
            );
            if (!acceptance.Matches(command, reference))
                throw new InvalidDataException("Notify acceptance evidence is incomplete or inconsistent.");

            return acceptance;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Notify email request was cancelled.", cancellationToken);
        }
        catch (Exception)
        {
            // HTTP and SDK exception text can contain the recipient, request or response. Keep it outside logs.
            throw new InvalidOperationException("Notify email request failed or returned invalid acceptance evidence.");
        }
    }

    private static Dictionary<string, dynamic> CreatePersonalisation(JsonElement values)
    {
        var personalisation = new Dictionary<string, dynamic>();
        foreach (var property in values.EnumerateObject())
            personalisation[property.Name] = new JRaw(property.Value.GetRawText());

        return personalisation;
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
