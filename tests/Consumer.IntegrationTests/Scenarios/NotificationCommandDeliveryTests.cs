using Amazon.SQS;
using Amazon.SQS.Model;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class NotificationCommandDeliveryTests : IntegrationTestBase
{
    private const string ConflictingIdempotencyKey = "conflicting-command-key-1";
    private const string DuplicateIdempotencyKey = "duplicate-command-key-1";
    private const string EmailAddress = "recipient@example.com";
    private const string IdempotencyKey = "command-key-1";
    private const string Personalisation = "secret personalisation";
    private const string TemplateId = "template-1";

    [Fact]
    public async Task WhenPreCutoverCommandIsReceived_ShouldRecordSuppressionAndDeleteMessage()
    {
        using var sqsClient = CreateSqsClient();
        await sqsClient.SendMessageAsync(
            new SendMessageRequest
            {
                QueueUrl = CommandQueueUrl,
                MessageBody = CommandBody(),
                MessageDeduplicationId = IdempotencyKey,
                MessageGroupId = "test-recipient-lane",
            },
            TestContext.Current.CancellationToken
        );
        using var mongoClient = CreateMongoClient();
        var records = mongoClient
            .GetDatabase("waste-obligations-notifications")
            .GetCollection<BsonDocument>("NotificationDeliveryRecord");
        var filter = NotificationKeyFilter(IdempotencyKey);

        await WaitForAsync(async () =>
        {
            var record = await records.Find(filter).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(record);
        });

        var storedRecord = await records.Find(filter).SingleAsync(TestContext.Current.CancellationToken);
        var storedJson = storedRecord.ToJson();

        Assert.Equal("delivery-suppressed", storedRecord["outcome"].AsString);
        Assert.Equal(
            [
                "_id",
                "actionOccurredAtUtc",
                "immutableFields",
                "notificationKey",
                "notificationType",
                "outcome",
                "recipient",
                "recordedAtUtc",
            ],
            storedRecord.Names.Order(StringComparer.Ordinal)
        );
        Assert.DoesNotContain(EmailAddress, storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(IdempotencyKey, storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Personalisation, storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(TemplateId, storedJson, StringComparison.Ordinal);

        await WaitForAsync(async () =>
        {
            var response = await sqsClient.ReceiveMessageAsync(
                new ReceiveMessageRequest { QueueUrl = CommandQueueUrl, WaitTimeSeconds = 0 },
                TestContext.Current.CancellationToken
            );

            Assert.Empty(response.Messages ?? []);
        });
    }

    [Fact]
    public async Task WhenPreCutoverCommandIsDuplicated_ShouldDeleteMessageAndKeepOneRecord()
    {
        using var sqsClient = CreateSqsClient();
        using var mongoClient = CreateMongoClient();
        var records = mongoClient
            .GetDatabase("waste-obligations-notifications")
            .GetCollection<BsonDocument>("NotificationDeliveryRecord");
        var filter = NotificationKeyFilter(DuplicateIdempotencyKey);

        await SendCommand(
            sqsClient,
            CommandBody(DuplicateIdempotencyKey),
            "duplicate-command-first",
            "duplicate-command-lane"
        );
        var originalRecord = await WaitForRecord(records, filter);

        await SendCommand(
            sqsClient,
            CommandBody(DuplicateIdempotencyKey),
            "duplicate-command-second",
            "duplicate-command-lane"
        );

        await WaitForAsync(async () =>
        {
            var attributes = await sqsClient.GetQueueAttributesAsync(
                new GetQueueAttributesRequest
                {
                    QueueUrl = CommandQueueUrl,
                    AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
                },
                TestContext.Current.CancellationToken
            );

            Assert.Equal("0", attributes.Attributes["ApproximateNumberOfMessages"]);
            Assert.Equal("0", attributes.Attributes["ApproximateNumberOfMessagesNotVisible"]);
        });

        var storedRecords = await records.Find(filter).ToListAsync(TestContext.Current.CancellationToken);
        var storedRecord = Assert.Single(storedRecords);

        Assert.Equal(originalRecord["_id"].AsObjectId, storedRecord["_id"].AsObjectId);
        Assert.Equal(originalRecord["immutableFields"].AsString, storedRecord["immutableFields"].AsString);
    }

    [Fact]
    public async Task WhenPreCutoverCommandConflicts_ShouldRetainMessageAndKeepOriginalRecord()
    {
        using var sqsClient = CreateSqsClient();
        using var mongoClient = CreateMongoClient();
        var records = mongoClient
            .GetDatabase("waste-obligations-notifications")
            .GetCollection<BsonDocument>("NotificationDeliveryRecord");
        var filter = NotificationKeyFilter(ConflictingIdempotencyKey);

        await SendCommand(
            sqsClient,
            CommandBody(ConflictingIdempotencyKey),
            "conflicting-command-first",
            "conflicting-command-lane"
        );
        var originalRecord = await WaitForRecord(records, filter);
        try
        {
            var conflictingBody = CommandBody(ConflictingIdempotencyKey, "template-2");
            await SendCommand(sqsClient, conflictingBody, "conflicting-command-second", "conflicting-command-lane");
            await WaitForAsync(async () =>
            {
                var attributes = await sqsClient.GetQueueAttributesAsync(
                    new GetQueueAttributesRequest
                    {
                        QueueUrl = CommandQueueUrl,
                        AttributeNames = ["ApproximateNumberOfMessagesNotVisible"],
                    },
                    TestContext.Current.CancellationToken
                );
                Assert.Equal("1", attributes.Attributes["ApproximateNumberOfMessagesNotVisible"]);
            });

            var storedRecords = await records.Find(filter).ToListAsync(TestContext.Current.CancellationToken);
            var storedRecord = Assert.Single(storedRecords);

            Assert.Equal(originalRecord["_id"].AsObjectId, storedRecord["_id"].AsObjectId);
            Assert.Equal(originalRecord["immutableFields"].AsString, storedRecord["immutableFields"].AsString);
        }
        finally
        {
            // This queue belongs to local Compose and tests run serially; remove the deliberately retained conflict.
            await sqsClient.PurgeQueueAsync(CommandQueueUrl, CancellationToken.None);
        }
    }

    private static async Task SendCommand(
        IAmazonSQS sqsClient,
        string body,
        string messageDeduplicationId,
        string messageGroupId
    )
    {
        await sqsClient.SendMessageAsync(
            new SendMessageRequest
            {
                QueueUrl = CommandQueueUrl,
                MessageBody = body,
                MessageDeduplicationId = messageDeduplicationId,
                MessageGroupId = messageGroupId,
            },
            TestContext.Current.CancellationToken
        );
    }

    private static async Task<BsonDocument> WaitForRecord(
        IMongoCollection<BsonDocument> records,
        FilterDefinition<BsonDocument> filter
    )
    {
        BsonDocument? record = null;

        await WaitForAsync(async () =>
        {
            record = await records.Find(filter).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(record);
        });

        return record!;
    }

    private static FilterDefinition<BsonDocument> NotificationKeyFilter(string idempotencyKey) =>
        Builders<BsonDocument>.Filter.Eq("notificationKey", CreateIdempotencyKeyDigest(idempotencyKey));

    private static string CreateIdempotencyKeyDigest(string idempotencyKey)
    {
        using var digest = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes("development-only-evidence-digest-secret")
        );
        var bytes = digest.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"v1:idempotency-key:{idempotencyKey}"));

        return $"v1:{Convert.ToHexStringLower(bytes)}";
    }

    private static string CommandBody(string idempotencyKey = IdempotencyKey, string templateId = TemplateId) =>
        $$"""
            {
              "schemaVersion": 1,
              "idempotencyKey": "{{idempotencyKey}}",
              "actionOccurredAtUtc": "2026-09-28T10:00:00Z",
              "notificationType": "declaration-submitted",
              "emailAddress": "{{EmailAddress}}",
              "templateId": "{{templateId}}",
              "personalisation": { "body": "{{Personalisation}}" }
            }
            """;
}
