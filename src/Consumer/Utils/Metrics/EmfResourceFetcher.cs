using System.Diagnostics;
using Amazon.CloudWatch.EMF.Environment;
using Newtonsoft.Json;

namespace Defra.WasteObligations.Consumer.Utils.Metrics;

public sealed class EmfResourceFetcher(HttpClient client, CancellationToken startupToken) : IResourceFetcher
{
    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(2);

    public T FetchJson<T>(Uri endpoint, string method, Dictionary<string, string>? header = null) =>
        JsonConvert.DeserializeObject<T>(FetchString(endpoint, method, header))!;

    public string FetchString(Uri endpoint, string method, Dictionary<string, string>? header = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(startupToken);
        timeout.CancelAfter(s_requestTimeout);
        using var request = new HttpRequestMessage(new HttpMethod(method), endpoint);
        foreach (var entry in header ?? [])
            request.Headers.Add(entry.Key, entry.Value);
        var startedAt = Stopwatch.GetTimestamp();
        using var response = client.Send(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        EnsureTimely(startedAt, timeout.Token);
        response.EnsureSuccessStatusCode();
        var body = response.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult();
        EnsureTimely(startedAt, timeout.Token);

        return body;
    }

    private static void EnsureTimely(long startedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(startedAt) >= s_requestTimeout)
            throw new TimeoutException("EMF metadata response exceeded its startup budget.");
    }
}
