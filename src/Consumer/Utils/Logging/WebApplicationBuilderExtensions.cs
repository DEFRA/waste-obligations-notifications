using System.Diagnostics.CodeAnalysis;
using Elastic.Serilog.Enrichers.Web;
using Microsoft.AspNetCore.HeaderPropagation;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

namespace Defra.WasteObligations.Consumer.Utils.Logging;

[ExcludeFromCodeCoverage]
public static class WebApplicationBuilderExtensions
{
    public static void ConfigureLoggingAndTracing(this WebApplicationBuilder builder, bool integrationTest = false)
    {
        builder.Services.AddHttpContextAccessor();
        builder
            .Services.AddOptions<TraceHeader>()
            .Bind(builder.Configuration)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSingleton<IConfigureOptions<HeaderPropagationOptions>>(serviceProvider =>
        {
            var traceHeader = serviceProvider.GetRequiredService<IOptions<TraceHeader>>().Value;
            return new ConfigureOptions<HeaderPropagationOptions>(options => options.Headers.Add(traceHeader.Name));
        });
        builder.Services.TryAddSingleton<HeaderPropagationValues>();
        builder.Services.AddSingleton<TraceIdReader>();

        if (!integrationTest)
        {
            builder.Host.UseSerilog(ConfigureLogging);
        }
    }

    private static void ConfigureLogging(
        HostBuilderContext hostBuilderContext,
        IServiceProvider services,
        LoggerConfiguration configuration
    )
    {
        var httpAccessor = services.GetRequiredService<IHttpContextAccessor>();
        var traceHeader = services.GetRequiredService<IOptions<TraceHeader>>().Value;
        var serviceVersion = Environment.GetEnvironmentVariable("SERVICE_VERSION") ?? "";

        configuration
            .ReadFrom.Configuration(hostBuilderContext.Configuration)
            .Enrich.WithEcsHttpContext(httpAccessor)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service.version", serviceVersion)
            .Enrich.WithCorrelationId(traceHeader.Name)
            .Filter.ByExcluding(logEvent =>
                logEvent.Level == LogEventLevel.Information
                && logEvent.Properties.TryGetValue("RequestPath", out var path)
                && path.ToString().Contains("/health")
                && !logEvent.MessageTemplate.Text.StartsWith("Request finished")
            );
    }
}
