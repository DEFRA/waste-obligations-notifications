using System.Net.Http.Json;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Utils.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Helpers;

public sealed class CancellationEmailFixture : IAsyncDisposable
{
    private readonly MongoClient _mongo = new("mongodb://localhost:27017");
    private readonly string _database = $"cancellation_integration_{Guid.NewGuid():N}";
    private readonly HttpClient _notify = new() { BaseAddress = new Uri("http://localhost:8086") };
    private WebApplication? _dependencies;
    private WebApplication? _host;
    public CancellationSqsClient Sqs { get; } = new();
    public CancellationHttpState State { get; }
    private INotificationCommandDigest Digest => _host!.Services.GetRequiredService<INotificationCommandDigest>();

    private CancellationEmailFixture(bool scheme, string country, bool deduplicate)
    {
        State = new CancellationHttpState { Scheme = scheme, Country = country };
        State.Persons =
        [
            new OrganisationPerson(State.SubmitterId, " Original ", " Submitter ", "submitter@example.test", "Other"),
            new OrganisationPerson(
                null,
                " Primary ",
                " Person ",
                deduplicate ? " SUBMITTER@example.test " : " primary@example.test ",
                "Approved Person"
            ),
        ];
    }

    public static async Task<CancellationEmailFixture> Create(
        bool scheme = false,
        string country = "GB-ENG",
        bool deduplicate = false
    )
    {
        var fixture = new CancellationEmailFixture(scheme, country, deduplicate);
        try
        {
            var token = TestContext.Current.CancellationToken;
            var suffix = Guid.NewGuid().ToString("N");
            fixture.Sqs.AnalyticsUrl = (
                await fixture.Sqs.CreateQueueAsync(
                    new CreateQueueRequest { QueueName = $"cancellation_analytics_{suffix}" },
                    token
                )
            ).QueueUrl;
            fixture.Sqs.CommandUrl = (
                await fixture.Sqs.CreateQueueAsync(
                    new CreateQueueRequest
                    {
                        QueueName = $"cancellation_commands_{suffix}.fifo",
                        Attributes = new() { ["FifoQueue"] = "true" },
                    },
                    token
                )
            ).QueueUrl;
            var dependencies = WebApplication.CreateBuilder();
            dependencies.Logging.ClearProviders();
            dependencies.WebHost.UseUrls("http://127.0.0.1:0");
            fixture._dependencies = dependencies.Build();
            fixture._dependencies.MapCancellationDependencies(fixture.State);
            await fixture._dependencies.StartAsync(token);
            var address = fixture._dependencies.Urls.Single();
            var values = new Dictionary<string, string?>
            {
                ["AWS_EMF_ENABLED"] = "false",
                ["AnalyticsEventConsumer:QueueUrl"] = fixture.Sqs.AnalyticsUrl,
                ["AnalyticsEventConsumer:ProcessingEnabled"] = "true",
                ["AnalyticsEventConsumer:WaitTimeSeconds"] = "1",
                ["AnalyticsEventConsumer:ReceiveTimeoutSeconds"] = "2",
                ["AnalyticsEventConsumer:ReadTimeoutSeconds"] = "5",
                ["AnalyticsEventConsumer:PublishTimeoutSeconds"] = "5",
                ["AnalyticsEventConsumer:DeleteTimeoutSeconds"] = "2",
                ["AnalyticsEventConsumer:SafetyHeadroomSeconds"] = "1",
                ["AnalyticsEventConsumer:VisibilityTimeoutSeconds"] = "20",
                ["AnalyticsEventConsumer:PollIntervalSeconds"] = "1",
                ["WasteObligations:BaseAddress"] = address,
                ["WasteObligations:Username"] = "isolated-reader",
                ["WasteObligations:Password"] = "synthetic-read-secret",
                ["WasteOrganisations:BaseAddress"] = address,
                ["WasteOrganisations:Username"] = "isolated-reader",
                ["WasteOrganisations:Password"] = "synthetic-read-secret",
                ["AccountBackend:BaseAddress"] = address,
                ["AccountBackend:TokenEndpoint"] = address + "/token",
                ["AccountBackend:ClientId"] = "isolated-reader",
                ["AccountBackend:ClientSecret"] = "synthetic-read-secret",
                ["NotificationCommandDelivery:QueueUrl"] = fixture.Sqs.CommandUrl,
                ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = "2026-09-01T00:00:00Z",
                ["NotificationCommandDelivery:EvidenceDigestSecret"] = "isolated-cancellation-evidence-secret",
                ["NotificationCommandDelivery:RecipientLaneSecret"] = "isolated-cancellation-lane-secret",
                ["NotificationCommandDelivery:WaitTimeSeconds"] = "0",
                ["NotificationCommandDelivery:PollIntervalSeconds"] = "1",
                ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
                ["Mongo:DatabaseName"] = fixture._database,
                ["Notify:ApiKey"] = await fixture._notify.GetStringAsync("/test/api-key", token),
                ["Notify:BaseAddress"] = "http://localhost:8086",
            };
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(values);
            builder.Services.AddAnalyticsEventConsumer(builder.Configuration);
            builder.Services.AddNotificationCommandDelivery(builder.Configuration);
            builder.Services.AddSingleton<IAmazonSQS>(fixture.Sqs);
            builder.Services.AddSingleton<IMongoClient>(fixture._mongo);
            builder.Services.AddHealth();
            fixture._host = builder.Build();
            fixture._host.MapHealth();
            await fixture._host.StartAsync(token);
            using var health = new HttpClient { BaseAddress = new Uri(fixture._host.Urls.Single()) };
            await health.WaitForCancellationHost(token);

            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public async Task SendAnalytics(
        string before = "Submitted",
        string after = "Cancelled",
        string eventType = "submission.amended",
        bool includeDeclarationSnapshot = false,
        int version = 2
    )
    {
        var declaration = State.Declaration() with { Version = version };
        if (eventType == "submission.created")
            declaration = declaration with
            {
                Audit = declaration.Audit!.Where(entry => entry.Action == "Submitted").ToArray(),
            };

        object afterSnapshot = includeDeclarationSnapshot
            ? new
            {
                status = after,
                declaration.Id,
                declaration.Version,
                declaration.ObligationYear,
                declaration.Organisation,
                declaration.Audit,
            }
            : new { status = after, organisation = new { id = State.OrganisationId } };

        await Sqs.SendMessageAsync(
            new SendMessageRequest
            {
                QueueUrl = Sqs.AnalyticsUrl,
                MessageBody = JsonSerializer.Serialize(
                    new
                    {
                        eventId = "01K5A7M9Q2X6F4N8C0V3B1D5E7",
                        entityId = $"cdec_{State.DeclarationId}",
                        entity = "compliance_declaration",
                        eventType,
                        schemaVersion = "compliance_declaration_v1.3",
                        version,
                        before = new { status = before },
                        after = afterSnapshot,
                    },
                    JsonSerializerOptions.Web
                ),
            },
            TestContext.Current.CancellationToken
        );
    }

    public async Task SendPublishedReplay(NotificationCommand command) =>
        await Sqs.SendMessageAsync(
            new SendMessageRequest
            {
                QueueUrl = Sqs.CommandUrl,
                MessageBody = JsonSerializer.Serialize(command, JsonSerializerOptions.Web),
                MessageGroupId = Digest.CreateRecipientLane(command.EmailAddress),
                MessageDeduplicationId = Guid.NewGuid().ToString("N"),
            },
            TestContext.Current.CancellationToken
        );

    public async Task RetryAnalytics() =>
        await Sqs.ChangeMessageVisibilityAsync(
            Sqs.AnalyticsUrl,
            Sqs.LastReceipt!,
            0,
            TestContext.Current.CancellationToken
        );

    public async Task<JsonElement[]> NotifyRequests()
    {
        var requests = await _notify.GetFromJsonAsync<JsonElement[]>(
            "/test/requests",
            TestContext.Current.CancellationToken
        );
        var references = Sqs
            .Confirmed.Select(command => Digest.CreateNotifyReference(command.IdempotencyKey))
            .ToHashSet();

        return requests!.Where(request => references.Contains(request.GetProperty("reference").GetString()!)).ToArray();
    }

    public async Task AssertAcceptedRecords(int count)
    {
        var records = await _mongo
            .GetDatabase(_database)
            .GetCollection<BsonDocument>("NotificationDeliveryRecord")
            .Find(new BsonDocument())
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(count, records.Count);
        Assert.All(records, record => Assert.Equal("delivery-accepted", record["outcome"].AsString));
    }

    public async Task AssertQueuesEmpty()
    {
        foreach (var queue in new[] { Sqs.AnalyticsUrl, Sqs.CommandUrl })
        {
            var result = await Sqs.GetQueueAttributesAsync(
                new GetQueueAttributesRequest
                {
                    QueueUrl = queue,
                    AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
                },
                TestContext.Current.CancellationToken
            );
            Assert.Equal("0", result.Attributes["ApproximateNumberOfMessages"]);
            Assert.Equal("0", result.Attributes["ApproximateNumberOfMessagesNotVisible"]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(CancellationToken.None);
            await _host.DisposeAsync();
        }
        if (_dependencies is not null)
        {
            await _dependencies.StopAsync(CancellationToken.None);
            await _dependencies.DisposeAsync();
        }
        foreach (var queue in new[] { Sqs.AnalyticsUrl, Sqs.CommandUrl }.Where(url => url.Length > 0))
            await Sqs.DeleteQueueAsync(queue, CancellationToken.None);
        await _mongo.DropDatabaseAsync(_database, CancellationToken.None);
        Sqs.Dispose();
        _mongo.Dispose();
        _notify.Dispose();
    }
}
