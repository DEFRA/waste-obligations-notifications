using System.Net;
using System.Text.Json;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Utils.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Tests.Data;

public sealed class MongoMigrationHealthTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenMigrationsHaveNotCompleted_ShouldExposeUnavailableWorkWithoutFailingLiveness(bool enabled)
    {
        var completion = new MongoMigrationCompletion();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [$"{NotificationCommandDeliveryOptions.SectionName}:ProcessingEnabled"] = enabled.ToString(),
            }
        );
        builder.Services.AddSingleton(completion);
        builder.Services.AddHealth(builder.Configuration);
        builder.Services.Configure<HealthCheckServiceOptions>(options =>
        {
            // Isolate the actual startup-health registration from independent dependency checks.
            foreach (
                var registration in options
                    .Registrations.Where(item => item.Name != "MongoMigrationCompletion")
                    .ToArray()
            )
                options.Registrations.Remove(registration);
        });
        await using var app = builder.Build();
        app.MapHealth();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        var registrations = app.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        Assert.Equal(enabled ? 1 : 0, registrations.Count);
        using var liveness = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        using var before = await client.GetAsync("/health/all", TestContext.Current.CancellationToken);
        Assert.Equal(enabled ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, before.StatusCode);
        if (enabled)
        {
            using var body = JsonDocument.Parse(
                await before.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
            );
            Assert.Equal(
                "Unhealthy",
                body.RootElement.GetProperty("results")
                    .GetProperty("MongoMigrationCompletion")
                    .GetProperty("status")
                    .GetString()
            );
        }
        completion.MarkCompleted();
        using var after = await client.GetAsync("/health/all", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }
}
