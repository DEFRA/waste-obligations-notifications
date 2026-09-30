using System.Globalization;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Tests.Delivery;

public sealed class ServiceCollectionExtensionsTests
{
    [Theory]
    [InlineData(20, 30, true)]
    [InlineData(20, 20, false)]
    [InlineData(20, 10, false)]
    [InlineData(0, 1, true)]
    [InlineData(0, 0, false)]
    public void WhenReceiveTimeoutIsConfigured_ShouldRequireItToExceedLongPollWait(
        int waitTimeSeconds,
        int receiveTimeoutSeconds,
        bool valid
    )
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["NotificationCommandDelivery:QueueUrl"] = "commands.fifo",
                    ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = "2100-01-01T00:00:00Z",
                    ["NotificationCommandDelivery:EvidenceDigestSecret"] = "test-evidence-secret",
                    ["NotificationCommandDelivery:RecipientLaneSecret"] = "test-lane-secret",
                    ["NotificationCommandDelivery:WaitTimeSeconds"] = waitTimeSeconds.ToString(
                        CultureInfo.InvariantCulture
                    ),
                    ["NotificationCommandDelivery:ReceiveTimeoutSeconds"] = receiveTimeoutSeconds.ToString(
                        CultureInfo.InvariantCulture
                    ),
                    ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddNotificationCommandDelivery(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NotificationCommandDeliveryOptions>>();

        if (valid)
        {
            Assert.Equal(waitTimeSeconds, options.Value.WaitTimeSeconds);
            Assert.Equal(receiveTimeoutSeconds, options.Value.ReceiveTimeoutSeconds);
            Assert.Equal(1, options.Value.BatchSize);
        }
        else
        {
            Assert.Throws<OptionsValidationException>(() => options.Value);
        }
    }
}
