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
    public void WhenSendingIsEnabled_ShouldRequireReceiveTimeoutToExceedLongPollWait(
        int waitTimeSeconds,
        int receiveTimeoutSeconds,
        bool valid
    )
    {
        using var provider = CreateProvider(true, waitTimeSeconds, receiveTimeoutSeconds);
        var options = provider.GetRequiredService<IOptions<NotificationCommandDeliveryOptions>>();

        if (valid)
        {
            Assert.True(options.Value.ProcessingEnabled);
            Assert.Equal(waitTimeSeconds, options.Value.WaitTimeSeconds);
            Assert.Equal(receiveTimeoutSeconds, options.Value.ReceiveTimeoutSeconds);
            Assert.Equal(1, options.Value.BatchSize);
            Assert.True(options.Value.HasValidProcessingBudget);
        }
        else
        {
            var exception = Assert.Throws<OptionsValidationException>(() => options.Value);
            Assert.Contains("Notification command receive timeout must exceed the long-poll wait", exception.Failures);
            Assert.DoesNotContain(
                exception.Failures,
                failure => failure.Contains("must cover", StringComparison.Ordinal)
            );
        }
    }

    [Theory]
    [InlineData(20, 20)]
    [InlineData(20, 10)]
    [InlineData(0, 0)]
    public void WhenSendingIsPaused_ShouldPermitUnusedReceiveBudgets(int waitTimeSeconds, int receiveTimeoutSeconds)
    {
        using var provider = CreateProvider(false, waitTimeSeconds, receiveTimeoutSeconds);
        var options = provider.GetRequiredService<IOptions<NotificationCommandDeliveryOptions>>().Value;

        Assert.False(options.ProcessingEnabled);
        Assert.Equal(waitTimeSeconds, options.WaitTimeSeconds);
        Assert.Equal(receiveTimeoutSeconds, options.ReceiveTimeoutSeconds);
    }

    private static ServiceProvider CreateProvider(
        bool processingEnabled,
        int waitTimeSeconds,
        int receiveTimeoutSeconds
    )
    {
        const string ampleBudgetSeconds = "1000";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AWS_EMF_ENABLED"] = "false",
                    ["NotificationCommandDelivery:ProcessingEnabled"] = processingEnabled.ToString(),
                    ["NotificationCommandDelivery:QueueUrl"] = "commands.fifo",
                    ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = "2100-01-01T00:00:00Z",
                    ["NotificationCommandDelivery:EvidenceDigestSecret"] = "test-evidence-secret",
                    ["NotificationCommandDelivery:RecipientLaneSecret"] = "test-lane-secret",
                    ["NotificationCommandDelivery:CommandLeaseSeconds"] = ampleBudgetSeconds,
                    ["NotificationCommandDelivery:VisibilityTimeoutSeconds"] = ampleBudgetSeconds,
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

        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("declaration-submitted", true)]
    [InlineData("synthetic-type@example.com", false)]
    [InlineData("Déclaration", false)]
    [InlineData("", false)]
    public void WhenDiagnosticLabelIsConfigured_ShouldRejectPrivateOrUnboundedCategories(string label, bool valid)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["NotificationCommandDelivery:QueueUrl"] = "commands.fifo",
                    ["NotificationCommandDelivery:EvidenceDigestSecret"] = "test-evidence-secret",
                    ["NotificationCommandDelivery:RecipientLaneSecret"] = "test-lane-secret",
                    ["NotificationCommandDelivery:DiagnosticNotificationTypes:0"] = label,
                    ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddNotificationCommandDelivery(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NotificationCommandDeliveryOptions>>();

        if (valid)
            Assert.Equal(label, options.Value.GetDiagnosticNotificationType(label));
        else
            Assert.Throws<OptionsValidationException>(() => options.Value);
    }
}
