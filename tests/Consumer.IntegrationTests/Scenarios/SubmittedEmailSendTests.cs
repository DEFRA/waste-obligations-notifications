using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
using Defra.WasteObligations.Consumer.Submission;
using Defra.WasteObligations.Consumer.Utils.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class SubmittedEmailSendTests : IntegrationTestBase
{
    [Theory]
    [InlineData(
        "DirectProducer",
        "GB-ENG",
        "11111111-1111-4111-8111-111111111111",
        "The Stored Regulator",
        "the Stored Regulator"
    )]
    [InlineData(
        "DirectProducer",
        "GB-WLS",
        "22222222-2222-4222-8222-222222222222",
        "Stored Regulator",
        "Stored Regulator"
    )]
    [InlineData(
        "ComplianceScheme",
        "GB-ENG",
        "33333333-3333-4333-8333-333333333333",
        "The Stored Regulator",
        "the Stored Regulator"
    )]
    [InlineData(
        "ComplianceScheme",
        "GB-WLS",
        "44444444-4444-4444-8444-444444444444",
        "Stored Regulator",
        "Stored Regulator"
    )]
    public async Task WhenSubmittedAnalyticsIsHandledAndReplayed_ShouldSendOneOriginalAuditEmail(
        string registration,
        string country,
        string expectedTemplate,
        string expectedLeading,
        string expectedInline
    )
    {
        await using var fixture = await SubmissionFixture.Create(registration, country);
        await fixture.SendAnalytics();
        await WaitForAsync(async () =>
        {
            Assert.Equal(1, fixture.Sqs.AnalyticsDeletions);
            Assert.Equal(1, fixture.Sqs.CommandDeletions);
            var record = await fixture.Record();
            Assert.Equal("delivery-accepted", record["outcome"].AsString);
            Assert.Equal("compliance-declaration", record["source"]["type"].AsString);
            Assert.Equal(fixture.DeclarationId, record["source"]["id"].AsString);
            Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", record["correlationId"].AsString);
            await fixture.AssertQueuesEmpty();
        });
        var request = Assert.Single(await fixture.NotifyRequests());
        Assert.Equal("original.submitter@example.test", request.GetProperty("email_address").GetString());
        Assert.Equal(expectedTemplate, request.GetProperty("template_id").GetString());
        var personalisation = request.GetProperty("personalisation");
        Assert.Equal(5, personalisation.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Number, personalisation.GetProperty("obligationYear").ValueKind);
        Assert.Equal(2026, personalisation.GetProperty("obligationYear").GetInt32());
        Assert.Equal(expectedLeading, personalisation.GetProperty("regulatorLeading").GetString());
        Assert.Equal(expectedInline, personalisation.GetProperty("regulatorInline").GetString());
        Assert.Equal("stored-regulator@example.test", personalisation.GetProperty("regulatorEmail").GetString());
        Assert.Equal("Original Submitter", personalisation.GetProperty("user").GetString());
        Assert.Equal(1, fixture.Reads.DeclarationReads);

        // A handled replay is acknowledged after confirmed publication. FIFO may deduplicate
        // that publication; a fresh transport copy also exercises the Mongo terminal guard.
        await fixture.SendAnalytics();
        await WaitForAsync(async () =>
        {
            Assert.Equal(2, fixture.Sqs.AnalyticsDeletions);
            Assert.Equal(2, fixture.Sqs.ConfirmedPublications);
            await fixture.AssertQueuesEmpty();
        });
        Assert.Single(await fixture.NotifyRequests());
        var deletedBeforeTransportCopy = fixture.Sqs.CommandDeletions;
        await fixture.SendPublishedReplay();
        await WaitForAsync(async () =>
        {
            Assert.Equal(deletedBeforeTransportCopy + 1, fixture.Sqs.CommandDeletions);
            await fixture.AssertQueuesEmpty();
        });
        Assert.Single(await fixture.NotifyRequests());
        Assert.Equal(2, fixture.Reads.DeclarationReads);
    }

    private sealed class SubmissionFixture : IAsyncDisposable
    {
        private readonly MongoClient _mongo = new("mongodb://localhost:27017");
        private readonly string _databaseName = $"submission_integration_{Guid.NewGuid():N}";
        private readonly HttpClient _http = new() { BaseAddress = new Uri("http://localhost:8086") };
        private readonly string _declarationId = ObjectId.GenerateNewId().ToString();
        public string DeclarationId => _declarationId;
        private readonly Guid _organisationId = Guid.NewGuid();
        private IHost? _host;
        private bool _started;
        public ObservingSqsClient Sqs { get; } = new();
        public DeclarationHandler Reads { get; private set; } = null!;
        private INotificationCommandDigest Digest => _host!.Services.GetRequiredService<INotificationCommandDigest>();
        private string Key => $"compliance-declaration:{_declarationId}:submitted:v1";

        public static async Task<SubmissionFixture> Create(string registration, string country)
        {
            var fixture = new SubmissionFixture();
            try
            {
                var token = TestContext.Current.CancellationToken;
                var suffix = Guid.NewGuid().ToString("N");
                fixture.Sqs.AnalyticsUrl = (
                    await fixture.Sqs.CreateQueueAsync(
                        new CreateQueueRequest
                        {
                            QueueName = $"submission_analytics_{suffix}",
                            Attributes = new() { ["VisibilityTimeout"] = "90" },
                        },
                        token
                    )
                ).QueueUrl;
                fixture.Sqs.CommandUrl = (
                    await fixture.Sqs.CreateQueueAsync(
                        new CreateQueueRequest
                        {
                            QueueName = $"submission_commands_{suffix}.fifo",
                            Attributes = new() { ["FifoQueue"] = "true", ["VisibilityTimeout"] = "120" },
                        },
                        token
                    )
                ).QueueUrl;
                fixture.Reads = new DeclarationHandler(
                    fixture._organisationId,
                    fixture._declarationId,
                    registration,
                    country
                );
                var values = new Dictionary<string, string?>
                {
                    ["WasteOrganisations:BaseAddress"] = "https://organisations.test/",
                    ["WasteOrganisations:Username"] = "isolated-reader",
                    ["WasteOrganisations:Password"] = "synthetic-read-secret",
                    ["AccountBackend:BaseAddress"] = "https://accounts.test/",
                    ["AccountBackend:TokenEndpoint"] = "https://accounts.test/token",
                    ["AccountBackend:ClientId"] = "isolated-reader",
                    ["AccountBackend:ClientSecret"] = "synthetic-read-secret",
                    ["AWS_EMF_ENABLED"] = "false",
                    ["AnalyticsEventConsumer:QueueUrl"] = fixture.Sqs.AnalyticsUrl,
                    ["AnalyticsEventConsumer:ProcessingEnabled"] = "true",
                    ["AnalyticsEventConsumer:WaitTimeSeconds"] = "1",
                    ["AnalyticsEventConsumer:PollIntervalSeconds"] = "1",
                    ["WasteObligations:BaseAddress"] = "https://declaration.test/",
                    ["WasteObligations:Username"] = "isolated-reader",
                    ["WasteObligations:Password"] = "synthetic-read-secret",
                    ["Notifications:Definitions:DeclarationSubmitted:Templates:DirectEnglishTemplateId"] =
                        "11111111-1111-4111-8111-111111111111",
                    ["Notifications:Definitions:DeclarationSubmitted:Templates:DirectWelshTemplateId"] =
                        "22222222-2222-4222-8222-222222222222",
                    ["Notifications:Definitions:DeclarationSubmitted:Templates:SchemeEnglishTemplateId"] =
                        "33333333-3333-4333-8333-333333333333",
                    ["Notifications:Definitions:DeclarationSubmitted:Templates:SchemeWelshTemplateId"] =
                        "44444444-4444-4444-8444-444444444444",
                    ["NotificationCommandDelivery:QueueUrl"] = fixture.Sqs.CommandUrl,
                    ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = "2026-09-01T00:00:00Z",
                    ["NotificationCommandDelivery:EvidenceDigestSecret"] = "isolated-submission-evidence-secret",
                    ["NotificationCommandDelivery:RecipientLaneSecret"] = "isolated-submission-lane-secret",
                    ["NotificationCommandDelivery:WaitTimeSeconds"] = "0",
                    ["NotificationCommandDelivery:PollIntervalSeconds"] = "1",
                    ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
                    ["Mongo:DatabaseName"] = fixture._databaseName,
                    ["Notify:ApiKey"] = await fixture._http.GetStringAsync("/test/api-key", token),
                    ["Notify:BaseAddress"] = "http://localhost:8086",
                };
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Configuration.AddInMemoryCollection(values);
                builder.Services.AddAnalyticsEventConsumer(builder.Configuration);
                builder.Services.AddNotificationCommandDelivery(builder.Configuration);
                builder
                    .Services.AddHttpClient<IWasteObligationsClient, WasteObligationsClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => fixture.Reads);
                builder.Services.AddSingleton<IAmazonSQS>(fixture.Sqs);
                builder.Services.AddSingleton<IMongoClient>(fixture._mongo);
                builder.Services.AddHealth();
                var app = builder.Build();
                app.MapHealth();
                fixture._host = app;
                await app.StartAsync(token);
                fixture._started = true;
                using var health = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
                await WaitForAsync(async () =>
                {
                    using var response = await health.GetAsync("/health", token);
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

        public async Task SendAnalytics() =>
            await Sqs.SendMessageAsync(
                new SendMessageRequest
                {
                    QueueUrl = Sqs.AnalyticsUrl,
                    MessageBody = $$$"""
                    {"eventId":"01K5A7M9Q2X6F4N8C0V3B1D5E7","correlationId":"4bf92f3577b34da6a3ce929d0e0e4736","entityId":"cdec_{{{_declarationId}}}","entity":"compliance_declaration","eventType":"submission.created","operation":"create","version":1,"schemaVersion":"compliance_declaration_v1.3","sequence":1,"occurredAt":"2026-09-20T11:12:13Z","recordedAt":"2026-09-20T11:12:14Z","actor":"2be16023-4b2b-4e6c-960a-0bc1a1162a84","after":{"organisation":{"id":"{{{_organisationId}}}","businessCountry":"GB-WLS","registrationType":"ComplianceScheme","regulator":"Changed Snapshot Regulator","regulatorEmail":"snapshot@example.test"},"submitterName":"Changed Snapshot Submitter"}}
                    """,
                },
                TestContext.Current.CancellationToken
            );

        public async Task SendPublishedReplay() =>
            await Sqs.SendMessageAsync(
                new SendMessageRequest
                {
                    QueueUrl = Sqs.CommandUrl,
                    MessageBody = Sqs.PublishedBody!,
                    MessageGroupId = Sqs.PublishedLane!,
                    MessageDeduplicationId = Guid.NewGuid().ToString("N"),
                },
                TestContext.Current.CancellationToken
            );

        public async Task<BsonDocument> Record() =>
            await _mongo
                .GetDatabase(_databaseName)
                .GetCollection<BsonDocument>("NotificationDeliveryRecord")
                .Find(new BsonDocument("notificationKey", Digest.CreateIdempotencyKeyDigest(Key)))
                .SingleAsync(TestContext.Current.CancellationToken);

        public async Task<JsonElement[]> NotifyRequests()
        {
            var requests = await _http.GetFromJsonAsync<JsonElement[]>(
                "/test/requests",
                TestContext.Current.CancellationToken
            );
            var reference = Digest.CreateNotifyReference(Key);

            return requests!.Where(request => request.GetProperty("reference").GetString() == reference).ToArray();
        }

        public async Task AssertQueuesEmpty()
        {
            foreach (var url in new[] { Sqs.AnalyticsUrl, Sqs.CommandUrl })
            {
                var response = await Sqs.GetQueueAttributesAsync(
                    new GetQueueAttributesRequest
                    {
                        QueueUrl = url,
                        AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
                    },
                    TestContext.Current.CancellationToken
                );
                Assert.Equal("0", response.Attributes["ApproximateNumberOfMessages"]);
                Assert.Equal("0", response.Attributes["ApproximateNumberOfMessagesNotVisible"]);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_host is not null)
            {
                if (_started)
                    await _host.StopAsync(CancellationToken.None);
                _host.Dispose();
            }
            foreach (var url in new[] { Sqs.AnalyticsUrl, Sqs.CommandUrl }.Where(url => url.Length > 0))
                await Sqs.DeleteQueueAsync(url, CancellationToken.None);
            Sqs.Dispose();
            await _mongo.DropDatabaseAsync(_databaseName, CancellationToken.None);
            _mongo.Dispose();
            _http.Dispose();
        }
    }

    private sealed class ObservingSqsClient : AmazonSQSClient
    {
        private int _analyticsDeletions;
        private int _commandDeletions;
        private int _confirmedPublications;
        public string AnalyticsUrl { get; set; } = "";
        public string CommandUrl { get; set; } = "";
        public string? PublishedBody { get; private set; }
        public string? PublishedLane { get; private set; }
        public int AnalyticsDeletions => Volatile.Read(ref _analyticsDeletions);
        public int CommandDeletions => Volatile.Read(ref _commandDeletions);
        public int ConfirmedPublications => Volatile.Read(ref _confirmedPublications);

        public ObservingSqsClient()
            : base(
                new BasicAWSCredentials("test", "test"),
                new AmazonSQSConfig { ServiceURL = "http://localhost:4566", AuthenticationRegion = "eu-west-2" }
            ) { }

        public override async Task<SendMessageResponse> SendMessageAsync(
            SendMessageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var response = await base.SendMessageAsync(request, cancellationToken);
            if (
                request.QueueUrl == CommandUrl
                && request.MessageDeduplicationId?.StartsWith("compliance-declaration:", StringComparison.Ordinal)
                    == true
            )
            {
                Assert.Equal(HttpStatusCode.OK, response.HttpStatusCode);
                Assert.False(string.IsNullOrWhiteSpace(response.MessageId));
                PublishedBody = request.MessageBody;
                PublishedLane = request.MessageGroupId;
                Interlocked.Increment(ref _confirmedPublications);
            }

            return response;
        }

        public override async Task<DeleteMessageResponse> DeleteMessageAsync(
            string queueUrl,
            string receiptHandle,
            CancellationToken cancellationToken = default
        )
        {
            if (queueUrl == AnalyticsUrl)
                Assert.True(ConfirmedPublications > AnalyticsDeletions);
            var response = await base.DeleteMessageAsync(queueUrl, receiptHandle, cancellationToken);
            if (queueUrl == AnalyticsUrl)
                Interlocked.Increment(ref _analyticsDeletions);
            else if (queueUrl == CommandUrl)
                Interlocked.Increment(ref _commandDeletions);

            return response;
        }
    }

    private sealed class DeclarationHandler(
        Guid organisationId,
        string declarationId,
        string registration,
        string country
    ) : HttpMessageHandler
    {
        private int _declarationReads;
        public int DeclarationReads => Volatile.Read(ref _declarationReads);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                $"/organisations/{organisationId:D}/compliance-declarations/{declarationId}",
                request.RequestUri!.AbsolutePath
            );
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal(
                Convert.ToBase64String(Encoding.UTF8.GetBytes("isolated-reader:synthetic-read-secret")),
                request.Headers.Authorization?.Parameter
            );
            Interlocked.Increment(ref _declarationReads);

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$$"""
                        {
                          "id":"{{{declarationId}}}","version":2,"created":"2026-09-20T11:12:13Z","updated":"2026-10-01T12:00:00Z","status":"Cancelled","obligationYear":2026,
                          "organisation":{"id":"{{{organisationId}}}","name":"Stored Organisation","registrationType":"{{{registration}}}","businessCountry":"{{{country}}}","complianceSchemeName":"Stored Scheme","schemeOperatorName":"Stored Operator","referenceNumber":"SYNTHETIC-001","address":{"addressLine1":"1 Test Street","town":"Test Town","postcode":"TE1 1ST","country":"GB"},"regulator":"Stored Regulator","regulatorEmail":"stored-regulator@example.test"},
                          "obligations":[{"material":"Plastic","recyclingTarget":0.5,"tonnages":{"material":100,"awaitingAcceptance":0,"accepted":50,"outstanding":0,"obligated":50},"status":"Met"}],
                          "obligationStatus":"Met","submitterName":"Changed Current Submitter","isRegulation43Compliant":true,"obligationCoveragePercentage":50,
                          "audit":[
                            {"action":"Submitted","timestamp":"2026-09-20T11:12:13Z","user":{"id":"5bd7385b-042e-464a-bba8-b9dcc0dce677","email":" Original.Submitter@Example.Test ","name":"Original Submitter","locale":"cy"}},
                            {"action":"Submitted","timestamp":"2026-09-21T12:00:00Z","user":{"id":"09fec9fa-b22d-4571-869e-68c5e0effe3e","email":"second@example.test","name":"Second Submitter","locale":"en"}},
                            {"action":"Cancelled","timestamp":"2026-10-01T12:00:00Z","user":{"id":"2be16023-4b2b-4e6c-960a-0bc1a1162a84","email":"later@example.test","name":"Later Actor","locale":"en"}}
                          ]
                        }
                        """
                    ),
                }
            );
        }
    }
}
