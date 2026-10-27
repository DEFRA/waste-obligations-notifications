using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Notifications;
using Defra.WasteObligations.Consumer.Scheduled;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
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

public sealed class NotSubmittedReminderFixture : IAsyncDisposable
{
    private readonly MongoClient _mongo = new("mongodb://localhost:27017");
    private readonly string _database = $"reminder_integration_{Guid.NewGuid():N}";
    private readonly HttpClient _notify = new() { BaseAddress = new Uri("http://localhost:8086") };
    private WebApplication? _dependencies;
    private WebApplication? _host;
    public CancellationSqsClient Sqs { get; } = new();
    public NotSubmittedReminderHttpState State { get; }
    private INotificationCommandDigest Digest => _host!.Services.GetRequiredService<INotificationCommandDigest>();

    private NotSubmittedReminderFixture(DateTimeOffset londonNow)
    {
        State = new NotSubmittedReminderHttpState { ObligationYear = londonNow.Year };
    }

    public static async Task<NotSubmittedReminderFixture> Create()
    {
        var londonNow = TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("Europe/London")
        );
        var fixture = new NotSubmittedReminderFixture(londonNow);
        try
        {
            var token = TestContext.Current.CancellationToken;
            fixture.Sqs.CommandUrl = (
                await fixture.Sqs.CreateQueueAsync(
                    new CreateQueueRequest
                    {
                        QueueName = $"reminder_commands_{Guid.NewGuid():N}.fifo",
                        Attributes = new() { ["FifoQueue"] = "true" },
                    },
                    token
                )
            ).QueueUrl;
            var dependencies = WebApplication.CreateBuilder();
            dependencies.Logging.ClearProviders();
            dependencies.WebHost.UseUrls("http://127.0.0.1:0");
            fixture._dependencies = dependencies.Build();
            fixture._dependencies.MapNotSubmittedReminderDependencies(fixture.State);
            await fixture._dependencies.StartAsync(token);
            var address = fixture._dependencies.Urls.Single();
            var values = new Dictionary<string, string?>
            {
                ["AWS_EMF_ENABLED"] = "false",
                ["WasteObligations:BaseAddress"] = address,
                ["WasteObligations:Username"] = "isolated-reader",
                ["WasteObligations:Password"] = "synthetic-read-secret",
                ["AccountBackend:BaseAddress"] = address,
                ["AccountBackend:TokenEndpoint"] = address + "/token",
                ["AccountBackend:ClientId"] = "isolated-reader",
                ["AccountBackend:ClientSecret"] = "synthetic-read-secret",
                ["NotificationCommandDelivery:QueueUrl"] = fixture.Sqs.CommandUrl,
                ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = "2020-01-01T00:00:00Z",
                ["NotificationCommandDelivery:EvidenceDigestSecret"] = "isolated-reminder-evidence-secret",
                ["NotificationCommandDelivery:RecipientLaneSecret"] = "isolated-reminder-lane-secret",
                ["NotificationCommandDelivery:WaitTimeSeconds"] = "0",
                ["NotificationCommandDelivery:PollIntervalSeconds"] = "1",
                ["NotSubmittedReminder:Enabled"] = "true",
                ["NotSubmittedReminder:Month"] = londonNow.Month.ToString(CultureInfo.InvariantCulture),
                ["NotSubmittedReminder:Day"] = londonNow.Day.ToString(CultureInfo.InvariantCulture),
                ["NotSubmittedReminder:Hour"] = "0",
                ["NotSubmittedReminder:Minute"] = "0",
                ["NotSubmittedReminder:SendWindow"] = "2.00:00:00",
                ["NotSubmittedReminder:PageSize"] = "1",
                ["NotSubmittedReminder:ObligationYearOverride"] = londonNow.Year.ToString(CultureInfo.InvariantCulture),
                ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
                ["Mongo:DatabaseName"] = fixture._database,
                ["Notify:ApiKey"] = await fixture._notify.GetStringAsync("/test/api-key", token),
                ["Notify:BaseAddress"] = "http://localhost:8086",
            };
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(values);
            builder.Services.AddWasteObligationsClient(builder.Configuration);
            builder.Services.AddNotificationDefinitions(builder.Configuration);
            builder.Services.AddNotificationCommandDelivery(builder.Configuration);
            builder.Services.AddNotSubmittedReminderProducer(builder.Configuration);
            builder.Services.Remove(
                builder.Services.Single(service => service.ImplementationType == typeof(NotSubmittedReminderWorker))
            );
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

    public Task<NotSubmittedReminderScanResult> Scan() =>
        _host!
            .Services.GetRequiredService<INotSubmittedReminderScanService>()
            .RunFullScan(TestContext.Current.CancellationToken);

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

    public async Task AssertCommandQueueEmpty()
    {
        var result = await Sqs.GetQueueAttributesAsync(
            new GetQueueAttributesRequest
            {
                QueueUrl = Sqs.CommandUrl,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            },
            TestContext.Current.CancellationToken
        );
        Assert.Equal("0", result.Attributes["ApproximateNumberOfMessages"]);
        Assert.Equal("0", result.Attributes["ApproximateNumberOfMessagesNotVisible"]);
    }

    public IReadOnlyList<NotificationCommand> ConfirmedCommands() => Sqs.Confirmed.ToArray();

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
        if (Sqs.CommandUrl.Length > 0)
            await Sqs.DeleteQueueAsync(Sqs.CommandUrl, CancellationToken.None);
        await _mongo.DropDatabaseAsync(_database, CancellationToken.None);
        Sqs.Dispose();
        _mongo.Dispose();
        _notify.Dispose();
    }
}
