using System.Net;
using Amazon.SQS;
using Defra.WasteObligations.Consumer.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests;

public class StartupTests : IClassFixture<ConsumerWebApplicationFactory>
{
    private readonly ConsumerWebApplicationFactory _factory;

    public StartupTests(ConsumerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_WhenTheApplicationStarts_ShouldBeHealthy()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

public class ConsumerWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AWS_EMF_ENABLED"] = "false",
                    ["AnalyticsEventConsumer:ProcessingEnabled"] = "false",
                    ["NotificationCommandDelivery:ProcessingEnabled"] = "false",
                    ["NotificationCommandDelivery:QueueUrl"] = "http://sqs.local/commands.fifo",
                    ["CommandDlqAdministration:QueueUrl"] = "http://sqs.local/commands-dlq.fifo",
                    ["NotificationCommandDelivery:EvidenceDigestSecret"] = "startup-test-evidence-secret",
                    ["NotificationCommandDelivery:RecipientLaneSecret"] = "startup-test-lane-secret",
                }
            )
        );
        builder.ConfigureTestServices(services =>
        {
            foreach (
                var descriptor in services
                    .Where(descriptor =>
                        descriptor.ServiceType == typeof(IHostedService)
                        && descriptor.ImplementationType == typeof(MongoMigrationService)
                    )
                    .ToArray()
            )
                services.Remove(descriptor);
            var completion = new MongoMigrationCompletion();
            completion.MarkCompleted();
            services.AddSingleton(completion);
            services.RemoveAll<IAmazonSQS>();
            services.AddSingleton(Substitute.For<IAmazonSQS>());
        });
    }
}
