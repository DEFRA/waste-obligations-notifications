using Amazon.SQS.Model;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class NotificationCommandDeliveryTests : IntegrationTestBase
{
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
            .GetDatabase("notifications")
            .GetCollection<BsonDocument>("notificationDeliveryRecords");

        await WaitForAsync(async () =>
        {
            var record = await records
                .Find(new BsonDocument())
                .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(record);
        });

        var storedRecord = await records.Find(new BsonDocument()).FirstAsync(TestContext.Current.CancellationToken);
        var storedJson = storedRecord.ToJson();

        Assert.Equal("delivery-suppressed", storedRecord["outcome"].AsString);
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

    private static string CommandBody() =>
        $$"""
            {
              "schemaVersion": 1,
              "idempotencyKey": "{{IdempotencyKey}}",
              "actionOccurredAtUtc": "2026-09-28T10:00:00Z",
              "notificationType": "declaration-submitted",
              "emailAddress": "{{EmailAddress}}",
              "templateId": "{{TemplateId}}",
              "personalisation": { "body": "{{Personalisation}}" }
            }
            """;
}
