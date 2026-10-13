using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Submission;
using Microsoft.AspNetCore.Http;
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
using Microsoft.Extensions.Logging;
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

    [Theory]
    [InlineData("DirectProducer", "GB-ENG", "cy", "11111111-1111-4111-8111-111111111111", "The Stored Regulator", "the Stored Regulator")]
    [InlineData("DirectProducer", "GB-WLS", "en", "22222222-2222-4222-8222-222222222222", "Stored Regulator", "Stored Regulator")]
    [InlineData("ComplianceScheme", "GB-ENG", "cy", "33333333-3333-4333-8333-333333333333", "The Stored Regulator", "the Stored Regulator")]
    [InlineData("ComplianceScheme", "GB-WLS", "en", "44444444-4444-4444-8444-444444444444", "Stored Regulator", "Stored Regulator")]
    public async Task WhenSubmissionIsConsumed_ShouldSendOriginalAuditAndStoredOrganisationValues(
        string registrationType,
        string country,
        string locale,
        string templateId,
        string regulatorLeading,
        string regulatorInline
    )
    {
        const string declarationId = "0123456789abcdef01234567";
        var organisationId = Guid.NewGuid();
        var declaration = JsonSerializer.SerializeToElement(new
        {
            id = declarationId,
            version = 3,
            status = "Cancelled",
            obligationYear = 2027,
            submitterName = "Incorrect declaration name",
            organisation = new
            {
                id = organisationId,
                registrationType,
                businessCountry = country,
                regulator = "Stored Regulator",
                regulatorEmail = "stored-regulator@example.com",
            },
            audit = new[]
            {
                new { action = "Submitted", timestamp = "2026-10-01T12:34:56.123Z", user = new { email = " Original.Submitter@Example.com ", name = "Original Submitter", locale } },
                new { action = "Submitted", timestamp = "2026-10-02T12:34:56Z", user = new { email = "later@example.com", name = "Later Actor", locale = "en" } },
                new { action = "Cancelled", timestamp = "2026-10-03T12:34:56Z", user = new { email = "cancelled@example.com", name = "Cancellation Actor", locale = "en" } },
            },
        });
        await using var fixture = await DeliveryFixture.Create(declaration: declaration, organisationId: organisationId);
        var key = $"submission:{organisationId:D}:{declarationId}";
        await fixture.Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = fixture.AnalyticsQueueUrl,
            MessageBody = JsonSerializer.Serialize(new
            {
                eventId = "01ARZ3NDEKTSV4RRFFQ69G5FAV",
                entityId = "cdec_" + declarationId,
                entity = "compliance_declaration",
                eventType = "submission.created",
                schemaVersion = "compliance_declaration_v1.3",
                version = 2,
                after = new { organisation = new { id = organisationId, businessCountry = "GB-SCT", regulator = "Current organisation regulator" } },
            }),
        }, TestContext.Current.CancellationToken);
        await WaitForAsync(async () =>
        {
            Assert.Equal(1, fixture.Sqs.AcceptedDeleteAttempts);
            Assert.Equal(1, fixture.Sqs.AnalyticsDeleteAttempts);
            Assert.Equal("delivery-accepted", (await fixture.Record(key))["outcome"].AsString);
        });
        Assert.Equal(1, fixture.DeclarationReadCount);
        var record = await fixture.Record(key);
        Assert.Equal(new DateTime(2026, 10, 1, 12, 34, 56, 123, DateTimeKind.Utc), record["actionOccurredAtUtc"].ToUniversalTime());
        var request = Assert.Single(await fixture.NotifyRequests(key));
        Assert.Equal("original.submitter@example.com", request.GetProperty("email_address").GetString());
        Assert.Equal(templateId, request.GetProperty("template_id").GetString());
        var values = request.GetProperty("personalisation");
        Assert.Equal(5, values.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Number, values.GetProperty("obligationYear").ValueKind);
        Assert.Equal(2027, values.GetProperty("obligationYear").GetInt32());
        Assert.Equal(regulatorLeading, values.GetProperty("regulatorLeading").GetString());
        Assert.Equal(regulatorInline, values.GetProperty("regulatorInline").GetString());
        Assert.Equal("stored-regulator@example.com", values.GetProperty("regulatorEmail").GetString());
        Assert.Equal("Original Submitter", values.GetProperty("user").GetString());
        foreach (var queue in new[] { fixture.AnalyticsQueueUrl, fixture.QueueUrl })
        {
            var attributes = await fixture.Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = queue,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            }, TestContext.Current.CancellationToken);
            Assert.Equal("0", attributes.Attributes["ApproximateNumberOfMessages"]);
            Assert.Equal("0", attributes.Attributes["ApproximateNumberOfMessagesNotVisible"]);
        }
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
        private WebApplication? _declarationHost;
        private int _declarationReadCount;
        public int DeclarationReadCount => Volatile.Read(ref _declarationReadCount);
        public string AnalyticsQueueUrl { get; private set; } = "";
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

        public static async Task<DeliveryFixture> Create(bool shortBudget = false, bool failFirstDelete = false, JsonElement? declaration = null, Guid? organisationId = null)
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
                if (declaration is { } body)
                {
                    fixture.AnalyticsQueueUrl = (await fixture.Sqs.CreateQueueAsync(new CreateQueueRequest
                    {
                        QueueName = $"submission_analytics_{suffix}",
                    }, token)).QueueUrl;
                    fixture.Sqs.AnalyticsQueueUrl = fixture.AnalyticsQueueUrl;
                    var dependencyBuilder = WebApplication.CreateBuilder();
                    dependencyBuilder.Logging.ClearProviders();
                    dependencyBuilder.WebHost.UseUrls("http://127.0.0.1:0");
                    var dependency = dependencyBuilder.Build();
                    fixture._declarationHost = dependency;
                    dependency.MapGet("/health", () => Results.Ok());
                    dependency.MapGet($"/organisations/{organisationId:D}/compliance-declarations/0123456789abcdef01234567", (HttpRequest request) =>
                    {
                        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("submission-client:synthetic-read-secret")), request.Headers.Authorization.ToString());
                        Interlocked.Increment(ref fixture._declarationReadCount);

                        return Results.Json(body);
                    });
                    await dependency.StartAsync(token);
                    values["AnalyticsEventConsumer:QueueUrl"] = fixture.AnalyticsQueueUrl;
                    values["AnalyticsEventConsumer:ProcessingEnabled"] = "true";
                    values["AnalyticsEventConsumer:WaitTimeSeconds"] = "1";
                    values["SubmissionEmail:ApiBaseAddress"] = dependency.Urls.Single();
                    values["SubmissionEmail:ClientId"] = "submission-client";
                    values["SubmissionEmail:ClientSecret"] = "synthetic-read-secret";
                    values["SubmissionEmail:DirectEnglishTemplateId"] = "11111111-1111-4111-8111-111111111111";
                    values["SubmissionEmail:DirectWelshTemplateId"] = "22222222-2222-4222-8222-222222222222";
                    values["SubmissionEmail:SchemeEnglishTemplateId"] = "33333333-3333-4333-8333-333333333333";
                    values["SubmissionEmail:SchemeWelshTemplateId"] = "44444444-4444-4444-8444-444444444444";
                }
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Configuration.AddInMemoryCollection(values);
                builder.Services.AddNotificationCommandDelivery(builder.Configuration);
                if (declaration.HasValue)
                    builder.Services.AddAnalyticsEventConsumer(builder.Configuration);
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

        public Task<BsonDocument> Record(NotificationCommand command) => Record(command.IdempotencyKey);

        public async Task<BsonDocument> Record(string key) =>
            await _mongo
                .GetDatabase(_databaseName)
                .GetCollection<BsonDocument>("NotificationDeliveryRecord")
                .Find(new BsonDocument("notificationKey", Digest.CreateIdempotencyKeyDigest(key)))
                .SingleAsync(TestContext.Current.CancellationToken);

        public Task<JsonElement[]> NotifyRequests(NotificationCommand command) => NotifyRequests(command.IdempotencyKey);

        public async Task<JsonElement[]> NotifyRequests(string key)
        {
            var requests = await _http.GetFromJsonAsync<JsonElement[]>(
                "/test/requests",
                TestContext.Current.CancellationToken
            );
            var reference = Digest.CreateNotifyReference(key);

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
            if (_declarationHost is not null)
            {
                await _declarationHost.StopAsync(CancellationToken.None);
                await _declarationHost.DisposeAsync();
            }
            if (Sqs is not null)
            {
                if (QueueUrl.Length > 0)
                    await Sqs.DeleteQueueAsync(QueueUrl, CancellationToken.None);
                if (DlqUrl.Length > 0)
                    await Sqs.DeleteQueueAsync(DlqUrl, CancellationToken.None);
                if (AnalyticsQueueUrl.Length > 0)
                    await Sqs.DeleteQueueAsync(AnalyticsQueueUrl, CancellationToken.None);
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
        private int _analyticsDeleteAttempts;
        public string AnalyticsQueueUrl { get; set; } = "";
        public int AnalyticsDeleteAttempts => Volatile.Read(ref _analyticsDeleteAttempts);
        public int AcceptedDeleteAttempts => Volatile.Read(ref _acceptedDeleteAttempts);

        public override async Task<DeleteMessageResponse> DeleteMessageAsync(
            string queueUrl,
            string receiptHandle,
            CancellationToken cancellationToken = default
        )
        {
            if (queueUrl == AnalyticsQueueUrl)
            {
                var response = await base.DeleteMessageAsync(queueUrl, receiptHandle, cancellationToken);
                Interlocked.Increment(ref _analyticsDeleteAttempts);

                return response;
            }
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
