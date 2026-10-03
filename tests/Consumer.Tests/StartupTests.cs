using System.Net;
using Amazon.SQS;
using Amazon.SQS.Model;
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
                    ["NotificationCommandDelivery:QueueUrl"] = "http://localhost:4566/commands.fifo",
                    ["CommandDlqAdministration:QueueUrl"] = "http://sqs.local/commands-dlq.fifo",
                    ["NotificationCommandDelivery:EvidenceDigestSecret"] = "test-evidence-secret",
                    ["NotificationCommandDelivery:RecipientLaneSecret"] = "test-lane-secret",
                    ["Notify:ApiKey"] = NotifyTestCredentials.ApiKey,
                    ["Notify:BaseAddress"] = "http://notify.local",
                    ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
                    ["Mongo:DatabaseName"] = "startup-test",
                }
            )
        );
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAmazonSQS>();
            var sqs = Substitute.For<IAmazonSQS>();
            sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());

                    return new ReceiveMessageResponse();
                });
            services.AddSingleton(sqs);
            foreach (
                var hosted in services
                    .Where(service =>
                        service.ServiceType == typeof(IHostedService)
                        && service.ImplementationType == typeof(MongoMigrationService)
                    )
                    .ToArray()
            )
                services.Remove(hosted);
            var completion = new MongoMigrationCompletion();
            completion.MarkCompleted();
            services.AddSingleton(completion);
        });
    }
}
