using System.Net.Http.Json;
using System.Text.Json;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Startup;
using Defra.WasteObligations.Consumer.Utils.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Notify.Client;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class NullCutoverDeliveryTests : IntegrationTestBase
{
    [Theory]
    [InlineData("2026-09-28T10:00:00Z")]
    [InlineData("2101-01-01T00:00:00Z")]
    public async Task WhenNullCutoverCommandBecomesSendEligible_ShouldPreserveSuppressionWithoutNotify(
        string actionTimestamp
    )
    {
        using var sqs = CreateSqsClient();
        using var mongo = CreateMongoClient();
        var token = TestContext.Current.CancellationToken;
        var commandKey = $"null-cutover-{Guid.NewGuid():N}";
        using var notifyHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri("http://localhost:8086"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var notify = new NotifyEmailClient(
            notifyHttp,
            Options.Create(
                new NotifyOptions
                {
                    ApiKey = await notifyHttp.GetStringAsync("/test/api-key", token),
                    BaseAddress = "http://localhost:8086",
                }
            ),
            (transport, options) => new NotificationClient(transport, options.ApiKey)
        );
        var databaseName = $"null_cutover_{Guid.NewGuid():N}";
        var queue = await sqs.CreateQueueAsync(
            new CreateQueueRequest
            {
                QueueName = $"null-cutover-{Guid.NewGuid():N}.fifo",
                Attributes = new() { ["FifoQueue"] = "true" },
            },
            token
        );
        var database = mongo.GetDatabase(databaseName);
        var readiness = new MongoMigrationCompletion();
        var startup = new ApplicationStartup();
        startup.MarkStarted();
        BsonDocument? original = null;
        try
        {
            await new MongoMigrationRunner(database, NullLogger<MongoMigrationRunner>.Instance, readiness).Run(token);
            var settings = new NotificationCommandDeliveryOptions
            {
                QueueUrl = queue.QueueUrl,
                EvidenceDigestSecret = "test-null-cutover-evidence-secret",
                RecipientLaneSecret = "test-null-cutover-lane-secret",
                WaitTimeSeconds = 0,
                PollIntervalSeconds = 1,
            };
            var digest = new NotificationCommandDigest(Options.Create(settings));
            var store = new MongoNotificationDeliveryRecordStore(
                mongo,
                Options.Create(
                    new MongoDbOptions { DatabaseUri = "mongodb://localhost:27017", DatabaseName = databaseName }
                ),
                digest
            );
            using var services = new ServiceCollection()
                .AddSingleton<INotificationDeliveryRecordStore>(store)
                .AddNotificationCommandMetrics()
                .BuildServiceProvider();
            var records = database.GetCollection<BsonDocument>("NotificationDeliveryRecord");
            foreach (var cutover in new string?[] { null, "2026-09-28T10:00:00Z" })
            {
                using var consumer = new NotificationCommandConsumer(
                    sqs,
                    Options.Create(settings with { EmailDeliveryCutoverUtc = cutover }),
                    new NotificationDeliveryRecordStoreFactory(services),
                    startup,
                    services.GetRequiredService<INotificationCommandMetrics>(),
                    NullLogger<NotificationCommandConsumer>.Instance,
                    notify,
                    digest
                );
                await consumer.StartAsync(token);
                await sqs.SendMessageAsync(
                    new SendMessageRequest
                    {
                        QueueUrl = queue.QueueUrl,
                        MessageGroupId = "null-cutover-test-lane",
                        MessageDeduplicationId = Guid.NewGuid().ToString(),
                        MessageBody = JsonSerializer.Serialize(
                            new
                            {
                                schemaVersion = 1,
                                idempotencyKey = commandKey,
                                actionOccurredAtUtc = actionTimestamp,
                                notificationType = "submitted",
                                emailAddress = "recipient@example.com",
                                templateId = "private-template",
                                personalisation = new { body = "private-body" },
                            }
                        ),
                    },
                    token
                );
                await WaitForAsync(async () =>
                {
                    Assert.Equal(
                        "delivery-suppressed",
                        (await records.Find(new BsonDocument()).SingleAsync(token))["outcome"].AsString
                    );
                    var attributes = await sqs.GetQueueAttributesAsync(
                        new GetQueueAttributesRequest
                        {
                            QueueUrl = queue.QueueUrl,
                            AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
                        },
                        token
                    );
                    Assert.Equal("0", attributes.Attributes["ApproximateNumberOfMessages"]);
                    Assert.Equal("0", attributes.Attributes["ApproximateNumberOfMessagesNotVisible"]);
                });
                await consumer.StopAsync(token);
                var record = await records.Find(new BsonDocument()).SingleAsync(token);
                Assert.Equal(8, record.ElementCount);
                if (original is null)
                    original = record;
                else
                    Assert.Equal(original, record);
                var requests = await notifyHttp.GetFromJsonAsync<JsonElement[]>("/test/requests", token);
                var reference = digest.CreateNotifyReference(commandKey);
                Assert.DoesNotContain(requests!, request => request.GetProperty("reference").GetString() == reference);
                foreach (var value in new[] { commandKey, "recipient@example.com", "private-template", "private-body" })
                    Assert.DoesNotContain(value, record.ToJson(), StringComparison.Ordinal);
            }
        }
        finally
        {
            await Task.WhenAll(
                mongo.DropDatabaseAsync(databaseName, CancellationToken.None),
                sqs.DeleteQueueAsync(queue.QueueUrl, CancellationToken.None)
            );
        }
    }
}
