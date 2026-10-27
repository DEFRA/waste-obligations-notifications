using System.Globalization;
using System.Net;
using System.Text;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.IntegrationTests.Helpers;
using Defra.WasteObligations.Consumer.Scheduled;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class NotSubmittedReminderAdminTests : IntegrationTestBase
{
    private const string Route = "/admin/not-submitted-reminder/scan";
    private const string Secret = "local-test-admin:secret";

    [Fact]
    public async Task WhenAdminRequestsScanInSendWindow_ShouldAcceptAndWakeWorkerToPublishReminders()
    {
        await using var host = await ReminderAdminHost.Start(TimeSpan.Zero);
        using var client = await host.CreateReadyClient();
        await WaitForAsync(() =>
        {
            Assert.Equal(1, host.State.UnsubmittedReads);

            return Task.CompletedTask;
        });
        var organisationId = host.State.AddOrganisation(
            "GB-ENG",
            Person("approved@example.test", "Approved Person"),
            Person("delegated@example.test", "Delegated Person")
        );

        using var anonymous = await client.SendAsync(Request(null), TestContext.Current.CancellationToken);
        using var accepted = await client.SendAsync(Request(Secret), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        await WaitForAsync(() =>
        {
            Assert.Equal(2, host.State.UnsubmittedReads);
            Assert.Equal(2, host.Sqs.Confirmed.Count);

            return Task.CompletedTask;
        });
        Assert.All(
            host.Sqs.Confirmed,
            command =>
                Assert.StartsWith(
                    $"not-submitted-reminder-1:{host.State.ObligationYear}:{organisationId:N}:",
                    command.IdempotencyKey
                )
        );
        Assert.Equal(
            new[] { "approved@example.test", "delegated@example.test" },
            host.Sqs.Confirmed.Select(command => command.EmailAddress).Order()
        );
    }

    [Fact]
    public async Task WhenAdminRequestsScanOutsideSendWindow_ShouldRejectWithReasonWithoutScanning()
    {
        await using var host = await ReminderAdminHost.Start(TimeSpan.FromDays(-10));
        using var client = await host.CreateReadyClient();
        host.State.AddOrganisation("GB-ENG", Person("approved@example.test", "Approved Person"));

        using var response = await client.SendAsync(Request(Secret), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(NotSubmittedReminderCutoverGuard.OutsideSendWindowReason, body, StringComparison.Ordinal);
        Assert.Equal(0, host.State.UnsubmittedReads);
        Assert.Empty(host.Sqs.Confirmed);
    }

    private static HttpRequestMessage Request(string? secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route);
        if (secret is not null)
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{secret}"))}"
            );

        return request;
    }

    private static OrganisationPerson Person(string email, string role) =>
        new(Guid.NewGuid(), "Synthetic", "Person", email, role);

    private sealed class ReminderAdminHost : IAsyncDisposable
    {
        private readonly MongoClient _mongo = new("mongodb://localhost:27017");
        private readonly string _database = $"reminder_admin_{Guid.NewGuid():N}";
        private WebApplication? _dependencies;
        private AdministrationApplicationFactory? _factory;
        public CancellationSqsClient Sqs { get; } = new();
        public NotSubmittedReminderHttpState State { get; }

        private ReminderAdminHost(int obligationYear)
        {
            State = new NotSubmittedReminderHttpState { ObligationYear = obligationYear };
        }

        public static async Task<ReminderAdminHost> Start(TimeSpan scheduleOffset)
        {
            var token = TestContext.Current.CancellationToken;
            var londonNow = TimeZoneInfo.ConvertTime(
                DateTimeOffset.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("Europe/London")
            );
            var scheduled = londonNow.Add(scheduleOffset);
            var host = new ReminderAdminHost(londonNow.Year);
            try
            {
                host.Sqs.CommandUrl = (
                    await host.Sqs.CreateQueueAsync(
                        new CreateQueueRequest
                        {
                            QueueName = $"reminder_admin_commands_{Guid.NewGuid():N}.fifo",
                            Attributes = new() { ["FifoQueue"] = "true" },
                        },
                        token
                    )
                ).QueueUrl;

                var dependencies = WebApplication.CreateBuilder();
                dependencies.Logging.ClearProviders();
                dependencies.WebHost.UseUrls("http://127.0.0.1:0");
                host._dependencies = dependencies.Build();
                host._dependencies.MapNotSubmittedReminderDependencies(host.State);
                await host._dependencies.StartAsync(token);

                var address = host._dependencies.Urls.Single();
                using var notify = new HttpClient { BaseAddress = new Uri("http://localhost:8086") };
                host._factory = new AdministrationApplicationFactory(
                    host.Sqs,
                    host._mongo,
                    new Dictionary<string, string?>
                    {
                        ["NotificationCommandDelivery:QueueUrl"] = host.Sqs.CommandUrl,
                        ["Mongo:DatabaseName"] = host._database,
                        ["Notify:ApiKey"] = await notify.GetStringAsync("/test/api-key", token),
                        ["WasteObligations:BaseAddress"] = address,
                        ["WasteObligations:Username"] = "isolated-reader",
                        ["WasteObligations:Password"] = "synthetic-read-secret",
                        ["AccountBackend:BaseAddress"] = address,
                        ["AccountBackend:TokenEndpoint"] = address + "/token",
                        ["AccountBackend:ClientId"] = "isolated-reader",
                        ["AccountBackend:ClientSecret"] = "synthetic-read-secret",
                        ["NotSubmittedReminder:Month"] = scheduled.Month.ToString(CultureInfo.InvariantCulture),
                        ["NotSubmittedReminder:Day"] = scheduled.Day.ToString(CultureInfo.InvariantCulture),
                        ["NotSubmittedReminder:ObligationYearOverride"] = londonNow.Year.ToString(
                            CultureInfo.InvariantCulture
                        ),
                    }
                );

                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public async Task<HttpClient> CreateReadyClient()
        {
            var client = _factory!.CreateClient();
            await WaitForAsync(
                async () =>
                {
                    using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.OK, health.StatusCode);
                },
                TimeSpan.FromSeconds(30)
            );

            return client;
        }

        public async ValueTask DisposeAsync()
        {
            if (_factory is not null)
                await _factory.DisposeAsync();
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
        }
    }

    private sealed class AdministrationApplicationFactory(
        CancellationSqsClient sqs,
        IMongoClient mongo,
        Dictionary<string, string?> overrides
    ) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                foreach (
                    var descriptor in services
                        .Where(descriptor =>
                            descriptor.ServiceType == typeof(IHostedService)
                            && descriptor.ImplementationType == typeof(NotificationCommandConsumer)
                        )
                        .ToArray()
                )
                    services.Remove(descriptor);
                services.RemoveAll<IMongoClient>();
                services.AddSingleton(mongo);
                services.RemoveAll<Amazon.SQS.IAmazonSQS>();
                services.AddSingleton<Amazon.SQS.IAmazonSQS>(sqs);
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var values = new Dictionary<string, string?>
            {
                ["AWS_EMF_ENABLED"] = "false",
                ["AnalyticsEventConsumer:ProcessingEnabled"] = "false",
                ["AnalyticsEventConsumer:QueueUrl"] = AnalyticsEventsQueueUrl,
                ["NotificationCommandDelivery:EvidenceDigestSecret"] = "local-reminder-admin-evidence-secret",
                ["NotificationCommandDelivery:RecipientLaneSecret"] = "local-reminder-admin-lane-secret",
                ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = "2020-01-01T00:00:00Z",
                ["CommandDlqAdministration:QueueUrl"] = CommandDeadLetterQueueUrl,
                ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
                ["Notify:BaseAddress"] = "http://localhost:8086",
                ["NotSubmittedReminder:Enabled"] = "true",
                ["NotSubmittedReminder:Hour"] = "0",
                ["NotSubmittedReminder:Minute"] = "0",
                ["NotSubmittedReminder:SendWindow"] = "2.00:00:00",
                ["Acl:Clients:admin:Type"] = "ApiKey",
                ["Acl:Clients:admin:Secret"] = Secret,
                ["Acl:Clients:admin:Scopes:0"] = "admin",
            };
            foreach (var entry in overrides)
                values[entry.Key] = entry.Value;
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(values));

            return base.CreateHost(builder);
        }
    }
}
