using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Utils.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class NotificationCommandSendTests : IntegrationTestBase
{
    [Fact]
    public async Task WhenNotifyAcceptsAndDeleteFails_ShouldPersistBeforeDeletionAndSuppressRealQueueRedelivery()
    {
        await using var fixture = await DeliveryFixture.Create(failFirstDelete: true);
        var command = DeliveryFixture.Command("submitted", "template-1");
        await fixture.Send(command, "lane-1");
        await WaitForAsync(async () =>
        {
            Assert.Equal(2, fixture.Sqs.AcceptedDeleteAttempts);
            var attributes = await fixture.Sqs.GetQueueAttributesAsync(
                new GetQueueAttributesRequest
                {
                    QueueUrl = fixture.QueueUrl,
                    AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
                },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("0", attributes.Attributes["ApproximateNumberOfMessages"]);
            Assert.Equal("0", attributes.Attributes["ApproximateNumberOfMessagesNotVisible"]);
        });

        var record = await fixture.Record(command);
        Assert.Equal("delivery-accepted", record["outcome"].AsString);
        Assert.Equal(1, record["templateVersion"].AsInt32);
        Assert.True(Guid.TryParse(record["notifyNotificationId"].AsString, out _));
        Assert.Equal(fixture.Digest.CreateNotifyReference(command.IdempotencyKey), record["notifyReference"].AsString);
        foreach (
            var protectedValue in new[]
            {
                command.IdempotencyKey,
                command.EmailAddress,
                "private-body",
                "controlled rendered content",
            }
        )
            Assert.DoesNotContain(protectedValue, record.ToJson(), StringComparison.Ordinal);
        var requests = await fixture.NotifyRequests(command);
        var request = Assert.Single(requests);
        Assert.Equal("recipient@example.com", request.GetProperty("email_address").GetString());
        Assert.Equal(command.TemplateId, request.GetProperty("template_id").GetString());
        Assert.Equal("private-body", request.GetProperty("personalisation").GetProperty("body").GetString());
    }

    [Fact]
    public async Task WhenOneRecipientLaneFails_ShouldAllowAnotherLaneAndResumeAfterRealDlqRedrive()
    {
        await using var fixture = await DeliveryFixture.Create(shortBudget: true);
        var failed = DeliveryFixture.Command("submitted", "failed-template");
        var blocked = DeliveryFixture.Command("cancelled", "template-1");
        var independent = DeliveryFixture.Command("submitted", "template-1");
        await fixture.Send(failed, "lane-a");
        await fixture.Send(blocked, "lane-a");
        await fixture.Send(independent, "lane-c");
        await WaitForAsync(async () =>
        {
            var record = await fixture.Record(independent);
            Assert.Equal("delivery-accepted", record["outcome"].AsString);
            Assert.Empty(await fixture.NotifyRequests(blocked));
            var attributes = await fixture.Sqs.GetQueueAttributesAsync(
                new GetQueueAttributesRequest
                {
                    QueueUrl = fixture.QueueUrl,
                    AttributeNames = ["ApproximateNumberOfMessagesNotVisible"],
                },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("1", attributes.Attributes["ApproximateNumberOfMessagesNotVisible"]);
        });
        await WaitForAsync(
            async () =>
            {
                var response = await fixture.Sqs.ReceiveMessageAsync(
                    new ReceiveMessageRequest { QueueUrl = fixture.DlqUrl, WaitTimeSeconds = 0 },
                    TestContext.Current.CancellationToken
                );
                var message = Assert.Single(response.Messages ?? []);
                Assert.Equal(
                    failed.IdempotencyKey,
                    JsonDocument.Parse(message.Body).RootElement.GetProperty("idempotencyKey").GetString()
                );
            },
            TimeSpan.FromSeconds(40)
        );
        await WaitForAsync(async () =>
            Assert.Equal("delivery-accepted", (await fixture.Record(blocked))["outcome"].AsString)
        );
        Assert.Equal(3, (await fixture.NotifyRequests(failed)).Length);
        var failedRecord = await fixture.Record(failed);
        Assert.Equal("delivery-pending", failedRecord["outcome"].AsString);
        Assert.False(failedRecord.Contains("acceptedAtUtc"));
    }

    [Fact]
    public async Task WhenNotifyResponseIsSlow_ShouldLeaveIndeterminateClaimAndQueueMessage()
    {
        await using var fixture = await DeliveryFixture.Create(shortBudget: true);
        var command = DeliveryFixture.Command("submitted", "slow-template");
        await fixture.Send(command, "slow-lane");
        await WaitForAsync(async () => Assert.Single(await fixture.NotifyRequests(command)));
        await Task.Delay(TimeSpan.FromSeconds(1.3), TestContext.Current.CancellationToken);
        var record = await fixture.Record(command);
        Assert.Equal("delivery-pending", record["outcome"].AsString);
        Assert.False(record.Contains("acceptedAtUtc"));
        Assert.Equal(0, fixture.Sqs.AcceptedDeleteAttempts);
        var attributes = await fixture.Sqs.GetQueueAttributesAsync(
            new GetQueueAttributesRequest
            {
                QueueUrl = fixture.QueueUrl,
                AttributeNames = ["ApproximateNumberOfMessagesNotVisible"],
            },
            TestContext.Current.CancellationToken
        );
        Assert.Equal("1", attributes.Attributes["ApproximateNumberOfMessagesNotVisible"]);
    }

    private sealed class DeliveryFixture : IAsyncDisposable
    {
        private const string DatabaseUri = "mongodb://localhost:27017";
        private const string EvidenceSecret = "isolated-test-evidence-secret";
        private static readonly JsonSerializerOptions s_commandJsonOptions = new(JsonSerializerDefaults.Web);
        private readonly MongoClient _mongo = new(DatabaseUri);
        private readonly string _databaseName = $"delivery_integration_{Guid.NewGuid():N}";
        private readonly HttpClient _http = new() { BaseAddress = new Uri("http://localhost:8086") };
        private IHost? _host;
        private bool _started;
        public ObservingSqsClient Sqs { get; private set; } = null!;
        public string QueueUrl { get; private set; } = "";
        public string DlqUrl { get; private set; } = "";
        public NotificationCommandDigest Digest { get; } =
            new(
                Microsoft.Extensions.Options.Options.Create(
                    new NotificationCommandDeliveryOptions
                    {
                        QueueUrl = "local",
                        EmailDeliveryCutoverUtc = "2026-09-29T00:00:00Z",
                        EvidenceDigestSecret = EvidenceSecret,
                        RecipientLaneSecret = "isolated-test-lane-secret",
                    }
                )
            );

        public static async Task<DeliveryFixture> Create(bool shortBudget = false, bool failFirstDelete = false)
        {
            var fixture = new DeliveryFixture();
            try
            {
                fixture.Sqs = new ObservingSqsClient(
                    fixture
                        ._mongo.GetDatabase(fixture._databaseName)
                        .GetCollection<BsonDocument>("NotificationDeliveryRecord"),
                    failFirstDelete
                );
                var token = TestContext.Current.CancellationToken;
                var suffix = Guid.NewGuid().ToString("N");
                fixture.DlqUrl = (
                    await fixture.Sqs.CreateQueueAsync(
                        new CreateQueueRequest
                        {
                            QueueName = $"delivery_test_dlq_{suffix}.fifo",
                            Attributes = new() { ["FifoQueue"] = "true" },
                        },
                        token
                    )
                ).QueueUrl;
                var dlqArn = (
                    await fixture.Sqs.GetQueueAttributesAsync(
                        new GetQueueAttributesRequest { QueueUrl = fixture.DlqUrl, AttributeNames = ["QueueArn"] },
                        token
                    )
                ).Attributes["QueueArn"];
                fixture.QueueUrl = (
                    await fixture.Sqs.CreateQueueAsync(
                        new CreateQueueRequest
                        {
                            QueueName = $"delivery_test_{suffix}.fifo",
                            Attributes = new()
                            {
                                ["FifoQueue"] = "true",
                                ["VisibilityTimeout"] = shortBudget ? "7" : "120",
                                ["RedrivePolicy"] = JsonSerializer.Serialize(
                                    new { deadLetterTargetArn = dlqArn, maxReceiveCount = 3 }
                                ),
                            },
                        },
                        token
                    )
                ).QueueUrl;
                var values = new Dictionary<string, string?>
                {
                    ["AWS_EMF_ENABLED"] = "false",
                    ["NotificationCommandDelivery:QueueUrl"] = fixture.QueueUrl,
                    ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = "2026-09-29T00:00:00Z",
                    ["NotificationCommandDelivery:EvidenceDigestSecret"] = EvidenceSecret,
                    ["NotificationCommandDelivery:RecipientLaneSecret"] = "isolated-test-lane-secret",
                    ["NotificationCommandDelivery:WaitTimeSeconds"] = "0",
                    ["NotificationCommandDelivery:PollIntervalSeconds"] = "1",
                    ["Mongo:DatabaseUri"] = DatabaseUri,
                    ["Mongo:DatabaseName"] = fixture._databaseName,
                    ["Notify:ApiKey"] = await fixture._http.GetStringAsync("/test/api-key", token),
                    ["Notify:BaseAddress"] = "http://localhost:8086",
                };
                if (shortBudget)
                {
                    foreach (
                        var field in new[]
                        {
                            "ClaimTimeoutSeconds",
                            "NotifyTimeoutSeconds",
                            "AcceptanceTimeoutSeconds",
                            "DeleteTimeoutSeconds",
                            "SafetyHeadroomSeconds",
                        }
                    )
                        values[$"NotificationCommandDelivery:{field}"] = "1";
                    values["NotificationCommandDelivery:ReceiveTimeoutSeconds"] = "2";
                    values["NotificationCommandDelivery:VisibilityTimeoutSeconds"] = "7";
                    values["NotificationCommandDelivery:CommandLeaseSeconds"] = "5";
                }
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Configuration.AddInMemoryCollection(values);
                builder.Services.AddNotificationCommandDelivery(builder.Configuration);
                builder.Services.AddSingleton<IAmazonSQS>(fixture.Sqs);
                builder.Services.AddSingleton<IMongoClient>(fixture._mongo);
                builder.Services.AddHealth();
                var app = builder.Build();
                app.MapHealth();
                fixture._host = app;
                await app.StartAsync(token);
                fixture._started = true;
                using var healthClient = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
                await WaitForAsync(async () =>
                {
                    using var response = await healthClient.GetAsync("/health", token);
                    response.EnsureSuccessStatusCode();
                });

                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public static NotificationCommand Command(string notificationType, string templateId) =>
            new(
                1,
                Guid.NewGuid().ToString("N"),
                new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero),
                notificationType,
                " Recipient@Example.com ",
                templateId,
                JsonDocument.Parse("{\"body\":\"private-body\"}").RootElement.Clone()
            );

        public async Task Send(NotificationCommand command, string lane) =>
            await Sqs.SendMessageAsync(
                new SendMessageRequest
                {
                    QueueUrl = QueueUrl,
                    MessageBody = JsonSerializer.Serialize(command, s_commandJsonOptions),
                    MessageDeduplicationId = command.IdempotencyKey,
                    MessageGroupId = lane,
                },
                TestContext.Current.CancellationToken
            );

        public async Task<BsonDocument> Record(NotificationCommand command) =>
            await _mongo
                .GetDatabase(_databaseName)
                .GetCollection<BsonDocument>("NotificationDeliveryRecord")
                .Find(new BsonDocument("notificationKey", Digest.CreateIdempotencyKeyDigest(command.IdempotencyKey)))
                .SingleAsync(TestContext.Current.CancellationToken);

        public async Task<JsonElement[]> NotifyRequests(NotificationCommand command)
        {
            var requests = await _http.GetFromJsonAsync<JsonElement[]>(
                "/test/requests",
                TestContext.Current.CancellationToken
            );
            var reference = Digest.CreateNotifyReference(command.IdempotencyKey);

            return requests!.Where(request => request.GetProperty("reference").GetString() == reference).ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            if (_host is not null)
            {
                if (_started)
                    await _host.StopAsync(CancellationToken.None);
                _host.Dispose();
            }
            if (Sqs is not null)
            {
                if (QueueUrl.Length > 0)
                    await Sqs.DeleteQueueAsync(QueueUrl, CancellationToken.None);
                if (DlqUrl.Length > 0)
                    await Sqs.DeleteQueueAsync(DlqUrl, CancellationToken.None);
                Sqs.Dispose();
            }
            await _mongo.DropDatabaseAsync(_databaseName, CancellationToken.None);
            _mongo.Dispose();
            _http.Dispose();
        }
    }

    private sealed class ObservingSqsClient(IMongoCollection<BsonDocument> records, bool failFirstDelete)
        : AmazonSQSClient(
            new BasicAWSCredentials("test", "test"),
            new AmazonSQSConfig { ServiceURL = "http://localhost:4566", AuthenticationRegion = "eu-west-2" }
        )
    {
        private int _acceptedDeleteAttempts;
        public int AcceptedDeleteAttempts => Volatile.Read(ref _acceptedDeleteAttempts);

        public override async Task<DeleteMessageResponse> DeleteMessageAsync(
            string queueUrl,
            string receiptHandle,
            CancellationToken cancellationToken = default
        )
        {
            var accepted = await records
                .Find(new BsonDocument("outcome", "delivery-accepted"))
                .FirstOrDefaultAsync(cancellationToken);
            Assert.NotNull(accepted);
            var attempt = Interlocked.Increment(ref _acceptedDeleteAttempts);
            if (failFirstDelete && attempt == 1)
            {
                // Fault injection only touches this test's queue and receipt, making its accepted duplicate retry observable.
                await ChangeMessageVisibilityAsync(queueUrl, receiptHandle, 0, cancellationToken);
                throw new InvalidOperationException("Controlled queue deletion failure.");
            }

            return await base.DeleteMessageAsync(queueUrl, receiptHandle, cancellationToken);
        }
    }
}
