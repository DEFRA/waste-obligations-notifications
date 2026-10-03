using Defra.WasteObligations.Consumer.Administration;
using Defra.WasteObligations.Consumer.Authentication;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Endpoints;
using Defra.WasteObligations.Consumer.Utils;
using Defra.WasteObligations.Consumer.Utils.Health;
using Defra.WasteObligations.Consumer.Utils.Logging;
using Defra.WasteObligations.Consumer.Utils.Metrics;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Configuration.AddEnvironmentVariables();
    builder.Services.LoadCustomTrustStoreFromEnvironment();
    builder.ConfigureLoggingAndTracing();
    builder.Services.AddProblemDetails();
    builder.Services.AddAuthenticationAuthorization(builder.Configuration);
    builder.Services.AddHealth();
    builder.Services.AddAnalyticsEventConsumer(builder.Configuration);
    builder.Services.AddNotificationCommandDelivery(builder.Configuration);
    builder.Services.AddCommandDlqAdministration(builder.Configuration);

    var app = builder.Build();

    app.UseNotificationCommandMetrics();
    var delivery = app
        .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationCommandDeliveryOptions>>()
        .Value;
    delivery.TryReadCutover(out var cutover);
    if (app.Logger.IsEnabled(LogLevel.Information))
    {
        app.Logger.LogInformation("Email delivery cutover {EmailDeliveryCutoverUtc}", cutover);
    }

    app.UseHeaderPropagation();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapHealth();
    app.MapApiEndpoints();

    await app.RunAsync();
}
catch (Exception exception)
{
    Log.Fatal(exception, "Application start-up failed");
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program
{
    protected Program() { }
}
