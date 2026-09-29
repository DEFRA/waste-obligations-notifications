using Defra.WasteObligations.Consumer.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.Tests.Data;

public sealed class MongoConfigurationTests
{
    [Fact]
    public void WhenAwsUriIsConfigured_ShouldCreateClientAndUsePrimaryReads()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Mongo:DatabaseUri"] =
                        "mongodb://localhost:27017/admin?authSource=$external&authMechanism=MONGODB-AWS&readPreference=secondaryPreferred",
                    ["Mongo:DatabaseName"] = "notifications",
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddMongo(configuration);
        using var serviceProvider = services.BuildServiceProvider();

        var client = serviceProvider.GetRequiredService<IMongoClient>();
        var database = serviceProvider.GetRequiredService<IMongoDatabase>();

        Assert.Equal("MONGODB-AWS", client.Settings.Credential.Mechanism);
        Assert.Equal("waste-obligiations-notifications-consumer", client.Settings.ApplicationName);
        Assert.Equal(ReadPreference.Primary, client.Settings.ReadPreference);
        Assert.Equal("notifications", database.DatabaseNamespace.DatabaseName);
    }
}
