using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Utils;
using Defra.WasteObligations.Consumer.Utils.Health;
using Defra.WasteObligations.Consumer.Utils.Logging;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Configuration.AddEnvironmentVariables();
    builder.Services.LoadCustomTrustStoreFromEnvironment();
    builder.ConfigureLoggingAndTracing();
    builder.Services.AddProblemDetails();
    builder.Services.AddAuthorization();
    builder.Services.AddHealth();
    builder.Services.AddAnalyticsEventConsumer(builder.Configuration);
    builder.Services.AddNotificationCommandDelivery(builder.Configuration);

    var app = builder.Build();

    var delivery = app
        .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationCommandDeliveryOptions>>()
        .Value;
    var cutoverValid = delivery.TryReadCutover(out var cutover);
    app.Logger.LogInformation(
        "Email delivery cutover {EmailDeliveryCutoverUtc}; cutover valid {CutoverValid}",
        cutover,
        cutoverValid
    );

    app.UseHeaderPropagation();
    app.UseAuthorization();
    app.MapHealth();

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
