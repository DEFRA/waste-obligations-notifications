using System.Net;
using System.Net.Http.Headers;
using Notify.Interfaces;

namespace Defra.WasteObligations.Consumer.Delivery;

public sealed class NotifySdkHttpClient : IHttpClient
{
    private readonly HttpClient _client;
    private readonly CancellationToken _cancellationToken;
    private readonly HttpStatusCode _expectedStatus;
    private HttpRequestMessage? _request;
    private HttpResponseMessage? _response;
    private string? _accept;
    private string? _userAgent;

    public NotifySdkHttpClient(HttpClient client, HttpStatusCode expectedStatus, CancellationToken cancellationToken)
    {
        _client = client;
        _cancellationToken = cancellationToken;
        _expectedStatus = expectedStatus;
        BaseAddress = client.BaseAddress ?? throw new InvalidOperationException("Notify transport is not configured.");
    }

    // The SDK's logical address must not replace the configured typed client's routing.
    public Uri BaseAddress { get; set; }

    public void SetClientBaseAddress()
    {
        // The shared HttpClient is configured once by DI; SDK construction never mutates it.
    }

    public void AddContentHeader(string header) => _accept = header;

    public void AddUserAgent(string userAgent) => _userAgent = userAgent;

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        if (_request is not null)
        {
            request.Dispose();
            throw new InvalidOperationException("Notify operation cannot issue another request.");
        }
        _request = request;
        _cancellationToken.ThrowIfCancellationRequested();
        if (_accept is not null)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(_accept));
        if (_userAgent is not null)
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        // Buffer with the operation token before the SDK's non-cancellable response read.
        _response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, _cancellationToken);
        _cancellationToken.ThrowIfCancellationRequested();
        if (_response.StatusCode != _expectedStatus)
            throw new InvalidOperationException("Notify operation did not confirm its expected result.");

        return _response;
    }

    public void Dispose()
    {
        _response?.Dispose();
        _request?.Dispose();
    }
}
