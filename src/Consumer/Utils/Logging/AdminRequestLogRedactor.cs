using Elastic.CommonSchema.Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Defra.WasteObligations.Consumer.Utils.Logging;

public sealed class AdminRequestLogRedactor(IHttpContextAccessor httpAccessor) : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var httpContext =
            logEvent.Properties.TryGetValue("HttpContext", out var context)
            && context is ScalarValue { Value: SpecialProperties.HttpContextEnrichments enrichments }
                ? enrichments
                : null;
        if (
            !IsAdminPath(httpAccessor.HttpContext?.Request.Path.Value)
            && !IsAdminPath(ReadPath(logEvent, "RequestPath"))
            && !IsAdminPath(ReadPath(logEvent, "Path"))
            && !IsAdminPath(httpContext?.Url?.Path)
        )
            return;
        if (logEvent.Properties.ContainsKey("QueryString"))
            logEvent.AddOrUpdateProperty(new LogEventProperty("QueryString", new ScalarValue("")));
        if (httpContext?.Url is not { } url)
            return;
        // The ECS enricher creates a detached carrier and URL snapshot for each event.
        url.Query = "";
        if (url.Full is { } full)
            url.Full = StripQuery(full);
        if (url.Original is { } original)
            url.Original = StripQuery(original);
    }

    private static bool IsAdminPath(string? path) =>
        path is not null
        && (
            path.Equals("/admin", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase)
        );

    private static string? ReadPath(LogEvent logEvent, string property) =>
        logEvent.Properties.TryGetValue(property, out var value) && value is ScalarValue { Value: string path }
            ? path
            : null;

    private static string StripQuery(string value)
    {
        var queryStart = value.IndexOf('?');

        return queryStart < 0 ? value : value[..queryStart];
    }
}
