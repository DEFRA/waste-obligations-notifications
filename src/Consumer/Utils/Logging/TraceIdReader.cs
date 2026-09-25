using Microsoft.AspNetCore.HeaderPropagation;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Utils.Logging;

public sealed class TraceIdReader(
    HeaderPropagationValues headerPropagationValues,
    IOptions<TraceHeader> traceHeaderOptions
)
{
    public string? Read()
    {
        if (headerPropagationValues.Headers is null)
        {
            return null;
        }

        if (!headerPropagationValues.Headers.TryGetValue(traceHeaderOptions.Value.Name, out var values))
        {
            return null;
        }

        var traceId = values.ToString();

        return string.IsNullOrWhiteSpace(traceId) ? null : traceId;
    }
}
