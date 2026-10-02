using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class NullCutoverDeliveryTests : IntegrationTestBase
{
    [Fact]
    public async Task WhenCutoverIsUnsetThenConfigured_ShouldDeleteCommandsAndPreserveTerminalSuppression()
    {
        using var sqs = CreateSqsClient();
        using var mongo = CreateMongoClient();
        var token = TestContext.Current.CancellationToken;
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
        BsonDocument? original = null;
        try
        {
            await new MongoMigrationRunner(database, NullLogger<MongoMigrationRunner>.Instance, readiness).Run(token);
            var settings = new NotificationCommandDeliveryOptions
            {
                ProcessingEnabled = true,
                QueueUrl = queue.QueueUrl,
                EvidenceDigestSecret = "test-null-cutover-evidence-secret",
                RecipientLaneSecret = "test-null-cutover-lane-secret",
                WaitTimeSeconds = 0,
                PollIntervalSeconds = 1,
            };
            var store = new MongoNotificationDeliveryRecordStore(
                mongo,
                Options.Create(
                    new MongoDbOptions { DatabaseUri = "mongodb://localhost:27017", DatabaseName = databaseName }
                ),
                new NotificationCommandDigest(Options.Create(settings))
            );
            using var services = new ServiceCollection()
                .AddSingleton<INotificationDeliveryRecordStore>(store)
                .BuildServiceProvider();
            var records = database.GetCollection<BsonDocument>("NotificationDeliveryRecord");
            foreach (var cutover in new string?[] { null, "2100-01-01T00:00:00Z" })
            {
                using var consumer = new NotificationCommandConsumer(
                    sqs,
                    Options.Create(settings with { EmailDeliveryCutoverUtc = cutover }),
                    new NotificationDeliveryRecordStoreFactory(services),
                    readiness,
                    new NotificationCommandMetrics(),
                    NullLogger<NotificationCommandConsumer>.Instance
                );
                await consumer.StartAsync(token);
                await sqs.SendMessageAsync(
                    new SendMessageRequest
                    {
                        QueueUrl = queue.QueueUrl,
                        MessageGroupId = "null-cutover-test-lane",
                        MessageDeduplicationId = Guid.NewGuid().ToString(),
                        MessageBody =
                            """{"schemaVersion":1,"idempotencyKey":"null-cutover-key","actionOccurredAtUtc":"2026-09-28T10:00:00Z","notificationType":"submitted","emailAddress":"recipient@example.com","templateId":"private-template","personalisation":{"body":"private-body"}}""",
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
                foreach (
                    var value in new[]
                    {
                        "null-cutover-key",
                        "recipient@example.com",
                        "private-template",
                        "private-body",
                    }
                )
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
