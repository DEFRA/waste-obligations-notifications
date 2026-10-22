namespace Defra.WasteObligations.Consumer.Tests.Helpers;

public static class HttpStubExtensions
{
    public static HttpClient CreateHttpStub(
        this Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send
    ) => new(new Handler(send)) { BaseAddress = new Uri("http://localhost/api/"), Timeout = Timeout.InfiniteTimeSpan };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => send(request, cancellationToken);
    }
}
