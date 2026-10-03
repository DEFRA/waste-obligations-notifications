using System.Net;
using System.Text.Json;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Utils.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Defra.WasteObligations.Consumer.Tests;

public sealed class EmailDeliveryCutoverHealthTests
{
    [Theory]
    [InlineData(true, null, null, true, "suppress-all")]
    [InlineData(true, "2027-01-01T00:00:00.1234567+00:00", "2027-01-01T00:00:00.1230000+00:00", true, "boundary")]
    [InlineData(false, null, null, true, "paused")]
    [InlineData(false, "2027-01-01T00:00:00Z", "2027-01-01T00:00:00.0000000+00:00", true, "paused")]
    [InlineData(false, "private@example.invalid", null, false, "invalid-unused")]
    public async Task WhenExtendedHealthRequested_ShouldReportEffectiveCutoverWithoutDependencyCalls(
        bool processingEnabled,
        string? configured,
        string? expected,
        bool valid,
        string mode
    )
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["NotificationCommandDelivery:ProcessingEnabled"] = processingEnabled.ToString(),
            }
        );
        builder.Services.AddSingleton(
            Microsoft.Extensions.Options.Options.Create(
                new NotificationCommandDeliveryOptions
                {
                    ProcessingEnabled = processingEnabled,
                    EmailDeliveryCutoverUtc = configured,
                    QueueUrl = "unused",
                    EvidenceDigestSecret = "unused",
                    RecipientLaneSecret = "unused",
                }
            )
        );
        builder.Services.AddHealth(builder.Configuration);
        builder.Services.Configure<HealthCheckServiceOptions>(options =>
        {
            foreach (var check in options.Registrations.Where(check => check.Name != "EmailDeliveryCutover").ToArray())
                options.Registrations.Remove(check);
        });
        await using var app = builder.Build();
        app.MapHealth();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/health/all", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        var result = json.RootElement.GetProperty("results").GetProperty("EmailDeliveryCutover");
        Assert.Equal("Healthy", result.GetProperty("status").GetString());
        var data = result.GetProperty("data");
        Assert.Equal(processingEnabled, data.GetProperty("processingEnabled").GetBoolean());
        Assert.Equal(expected, data.GetProperty("emailDeliveryCutoverUtc").GetString());
        Assert.Equal(valid, data.GetProperty("cutoverValid").GetBoolean());
        Assert.Equal(mode, data.GetProperty("mode").GetString());
        Assert.DoesNotContain("private@example.invalid", body);
        using var ready = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
