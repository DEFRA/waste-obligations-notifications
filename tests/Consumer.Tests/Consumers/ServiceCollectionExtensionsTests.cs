using Amazon.SQS;
using Defra.WasteObligations.Consumer.Consumers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Tests.Consumers;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAnalyticsEventConsumer_ShouldRegisterTheConsumerAndOptions()
    {
        var services = new ServiceCollection();

        services.AddAnalyticsEventConsumer(CreateConfiguration());
        using var serviceProvider = services.BuildServiceProvider();

        Assert.Contains(
            services,
            service =>
                service.ServiceType == typeof(IHostedService)
                && service.ImplementationType == typeof(AnalyticsEventConsumer)
        );
        Assert.Contains(services, service => service.ServiceType == typeof(IAmazonSQS));

        var options = serviceProvider.GetRequiredService<IOptions<AnalyticsEventConsumerOptions>>().Value;

        Assert.Equal("http://localhost:4566/000000000000/analytics-events", options.QueueUrl);
        Assert.True(options.ProcessingEnabled);
        Assert.Equal(2, options.BatchSize);
        Assert.Equal(3, options.WaitTimeSeconds);
        Assert.Equal(4, options.PollIntervalSeconds);
    }

    private static IConfiguration CreateConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            [$"{AnalyticsEventConsumerOptions.SectionName}:QueueUrl"] =
                "http://localhost:4566/000000000000/analytics-events",
            [$"{AnalyticsEventConsumerOptions.SectionName}:ProcessingEnabled"] = "true",
            [$"{AnalyticsEventConsumerOptions.SectionName}:BatchSize"] = "2",
            [$"{AnalyticsEventConsumerOptions.SectionName}:WaitTimeSeconds"] = "3",
            [$"{AnalyticsEventConsumerOptions.SectionName}:PollIntervalSeconds"] = "4",
        };

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
