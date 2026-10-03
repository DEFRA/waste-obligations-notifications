using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Utils.Metrics;

public static class ApplicationBuilderExtensions
{
    public static IApplicationBuilder UseNotificationCommandMetrics(this IApplicationBuilder builder)
    {
        var options = builder.ApplicationServices.GetRequiredService<IOptions<EmfOptions>>().Value;
        if (options.Enabled)
            MetricsExporter.Init(
                builder.ApplicationServices.GetRequiredService<ILoggerFactory>(),
                options.EffectiveNamespace!
            );

        return builder;
    }
}
