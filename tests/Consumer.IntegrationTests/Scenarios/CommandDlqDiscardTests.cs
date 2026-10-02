using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Administration;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class CommandDlqDiscardTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);
    private const string Secret = "local-test-admin:secret";
    private const string PrivateContent = "private-redrive-personalisation-template";

    [Theory]
    [InlineData("new", false)]
    [InlineData("new", true)]
    [InlineData("expired", false)]
    [InlineData("delete-failure", false)]
    [InlineData("late-write", false)]
    [InlineData("active", false)]
    [InlineData("accepted", false)]
    [InlineData("suppressed", false)]
    [InlineData("conflict", false)]
    [InlineData("unknown", false)]
    public async Task WhenSelectedCommandIsDiscarded_ShouldPersistBeforeRemovalAndPreserveOtherMessagesAndProtectedHistory(
        string state,
        bool oauth
    )
    {
        using var mongo = CreateMongoClient();
        var databaseName = $"notifications_discard_{Guid.NewGuid():N}";
        var database = mongo.GetDatabase(databaseName);
        using var sqs = new ReplaySqsClient(database);
        var queueName = $"discard_dlq_{Guid.NewGuid():N}.fifo";
        var destinationName = $"discard_destination_{Guid.NewGuid():N}.fifo";
        CreateQueueResponse? queue = null;
        CreateQueueResponse? destination = null;
        Exception? testFailure = null;
        var failures = new List<Exception>();
        var token = TestContext.Current.CancellationToken;
        try
        {
            queue = await sqs.CreateQueueAsync(
                new CreateQueueRequest
                {
                    QueueName = queueName,
                    Attributes = new() { ["FifoQueue"] = "true" },
                },
                token
            );
            destination = await sqs.CreateQueueAsync(
                new CreateQueueRequest
                {
                    QueueName = destinationName,
                    Attributes = new() { ["FifoQueue"] = "true" },
                },
                token
            );
            await using var first = new AdministrationApplicationFactory(
                queue.QueueUrl,
                destination.QueueUrl,
                databaseName,
                sqs,
                mongo,
                120,
                oauth: oauth
            );
            using var firstClient = first.CreateClient();
            await first
                .Services.GetRequiredService<MongoMigrationReadiness>()
                .Wait(token)
                .WaitAsync(TimeSpan.FromSeconds(10), token);
            var command = Command("private-original-key@example.com", "private-recipient@example.com");
            var digest = first.Services.GetRequiredService<INotificationCommandDigest>();
            var store = first.Services.GetRequiredService<INotificationDeliveryRecordStore>();
            var records = database.GetCollection<BsonDocument>("NotificationDeliveryRecord");
            if (state == "suppressed")
                await store.RecordSuppression(command, token);
            if (state is "active" or "expired" or "accepted" or "unknown" or "conflict")
                await store.Claim(
                    state == "conflict" ? command with { TemplateId = "different-private-template" } : command,
                    "real-attempt",
                    60,
                    token
                );
            if (state == "expired")
                await records.UpdateOneAsync(
                    FilterDefinition<BsonDocument>.Empty,
                    new BsonDocument("$set", new BsonDocument("leaseExpiresAtUtc", DateTime.UnixEpoch)),
                    cancellationToken: token
                );
            if (state == "unknown")
                await records.UpdateOneAsync(
                    FilterDefinition<BsonDocument>.Empty,
                    new BsonDocument("$set", new BsonDocument("outcome", "private-unknown-outcome")),
                    cancellationToken: token
                );
            if (state == "accepted")
                Assert.True(
                    await store.RecordAcceptance(
                        command,
                        "real-attempt",
                        new NotifyAcceptance(
                            Guid.NewGuid().ToString(),
                            digest.CreateNotifyReference(command.IdempotencyKey),
                            command.TemplateId,
                            1
                        ),
                        token
                    )
                );
            var before = await records.Find(FilterDefinition<BsonDocument>.Empty).FirstOrDefaultAsync(token);
            var body = JsonSerializer.Serialize(command, s_jsonOptions);
            await Publish(
                sqs,
                queue.QueueUrl,
                body,
                [],
                command.IdempotencyKey,
                digest.CreateRecipientLane(command.EmailAddress),
                token
            );
            using var inspectionRequest = Request("inspect", oauth: oauth);
            using var inspected = await firstClient.SendAsync(inspectionRequest, token);
            Assert.Equal(HttpStatusCode.OK, inspected.StatusCode);
            using var inspection = JsonDocument.Parse(await inspected.Content.ReadAsStringAsync(token));
            var selectionToken = inspection.RootElement.GetProperty("selectionToken").GetString();
            Assert.NotNull(first.Services.GetRequiredService<CommandDlqSelectionTokens>().Validate(selectionToken));
            var unrelated = Command("other-private-key@example.com", "other-private-recipient@example.com");
            await Publish(
                sqs,
                queue.QueueUrl,
                JsonSerializer.Serialize(unrelated, s_jsonOptions),
                [],
                unrelated.IdempotencyKey,
                digest.CreateRecipientLane(unrelated.EmailAddress),
                token
            );
            sqs.ExpectedNotificationKey = digest.CreateIdempotencyKeyDigest(command.IdempotencyKey);
            sqs.ObserveMutation = true;
            sqs.FailNextDelete = state == "delete-failure";
            await using var second = new AdministrationApplicationFactory(
                queue.QueueUrl,
                destination.QueueUrl,
                databaseName,
                sqs,
                mongo,
                60,
                lateWrite: state == "late-write",
                oauth: oauth
            );
            using var secondClient = second.CreateClient();
            using var discardRequest = Request("discard", selectionToken, oauth: oauth);
            using var discarded = await secondClient.SendAsync(discardRequest, token);
            var eligible = state is "new" or "expired" or "delete-failure" or "late-write";
            var expectedStatus = state switch
            {
                "delete-failure" or "late-write" => HttpStatusCode.ServiceUnavailable,
                "new" or "expired" => HttpStatusCode.NoContent,
                _ => HttpStatusCode.Conflict,
            };
            Assert.Equal(expectedStatus, discarded.StatusCode);
            Assert.Equal(0, sqs.MutationPublications);
            Assert.Equal(0, await Count(sqs, destination.QueueUrl, token));
            var record = await records.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(token);
            if (eligible)
            {
                Assert.Equal("delivery-abandoned", record["outcome"].AsString);
                Assert.Equal(8, record.ElementCount);
                Assert.Equal("other", record["notificationType"].AsString);
                foreach (
                    var value in new[]
                    {
                        command.IdempotencyKey,
                        command.EmailAddress,
                        command.NotificationType,
                        PrivateContent,
                    }
                )
                    Assert.DoesNotContain(value, record.ToJson(), StringComparison.Ordinal);
                Assert.False(record.Contains("attemptOwner"));
                if (before is not null)
                    Assert.Equal(before["_id"], record["_id"]);
                if (state is "delete-failure" or "late-write")
                {
                    Assert.Equal(2, await Count(sqs, queue.QueueUrl, token));
                    Assert.Equal(state == "late-write" ? 0 : 1, sqs.SelectedDeletes);
                    using var retryRequest = Request("discard", selectionToken, oauth: oauth);
                    using var retried = await firstClient.SendAsync(retryRequest, token);
                    Assert.Equal(HttpStatusCode.NoContent, retried.StatusCode);
                    Assert.Equal(record, await records.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(token));
                }
                Assert.Equal(1, await Count(sqs, queue.QueueUrl, token));
                var remaining = Assert.Single(
                    (
                        await sqs.ReceiveMessageAsync(
                            new ReceiveMessageRequest
                            {
                                QueueUrl = queue.QueueUrl,
                                MaxNumberOfMessages = 1,
                                WaitTimeSeconds = 0,
                            },
                            token
                        )
                    ).Messages
                );
                Assert.Equal(unrelated.IdempotencyKey, NotificationCommandMessageReader.Read(remaining).IdempotencyKey);
                if (state == "new")
                    await VerifyFutureDuplicate(
                        sqs,
                        mongo,
                        destination.QueueUrl,
                        databaseName,
                        body,
                        command,
                        digest,
                        token
                    );
            }
            else
            {
                Assert.Equal(before, record);
                Assert.Equal(2, await Count(sqs, queue.QueueUrl, token));
                Assert.Equal(0, sqs.SelectedDeletes);
            }
            foreach (
                var value in new[]
                {
                    command.IdempotencyKey,
                    command.EmailAddress,
                    command.NotificationType,
                    PrivateContent,
                    selectionToken!,
                    Secret,
                    sqs.SelectedReceipt!,
                    "private-unknown-outcome",
                }
            )
            {
                Assert.DoesNotContain(
                    value,
                    await discarded.Content.ReadAsStringAsync(token),
                    StringComparison.Ordinal
                );
                Assert.All(
                    first.Logs.Messages.Concat(second.Logs.Messages),
                    log => Assert.DoesNotContain(value, log, StringComparison.Ordinal)
                );
            }
            Assert.Equal(0, sqs.MutationPublications);
        }
        catch (Exception exception)
        {
            testFailure = exception;
        }
        finally
        {
            await Cleanup(cleanup => RemoveQueue(sqs, queueName, queue?.QueueUrl, cleanup), "discard DLQ", failures);
            await Cleanup(
                cleanup => RemoveQueue(sqs, destinationName, destination?.QueueUrl, cleanup),
                "discard command queue",
                failures
            );
            await Cleanup(cleanup => mongo.DropDatabaseAsync(databaseName, cleanup), "discard database", failures);
        }
        if (failures.Count > 0)
        {
            if (testFailure is not null)
                failures.Insert(0, testFailure);
            throw new AggregateException("Owned discard resources could not all be cleaned up.", failures);
        }
        if (testFailure is not null)
            ExceptionDispatchInfo.Capture(testFailure).Throw();
    }

    private static async Task VerifyFutureDuplicate(
        ReplaySqsClient sqs,
        IMongoClient mongo,
        string queue,
        string database,
        string body,
        NotificationCommand command,
        INotificationCommandDigest digest,
        CancellationToken token
    )
    {
        using var fixture = new HttpClient { BaseAddress = new Uri("http://localhost:8086") };
        var values = new Dictionary<string, string?>
        {
            ["AWS_EMF_ENABLED"] = "false",
            ["NotificationCommandDelivery:QueueUrl"] = queue,
            ["NotificationCommandDelivery:ProcessingEnabled"] = "true",
            ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = "2026-09-29T00:00:00Z",
            ["NotificationCommandDelivery:EvidenceDigestSecret"] = "local-inspection-evidence-secret",
            ["NotificationCommandDelivery:RecipientLaneSecret"] = "local-inspection-lane-secret",
            ["NotificationCommandDelivery:WaitTimeSeconds"] = "0",
            ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
            ["Mongo:DatabaseName"] = database,
            ["Notify:ApiKey"] = await fixture.GetStringAsync("/test/api-key", token),
            ["Notify:BaseAddress"] = "http://localhost:8086",
        };
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(values))
            .ConfigureServices(
                (context, services) =>
                {
                    services.AddNotificationCommandDelivery(context.Configuration);
                    services.AddSingleton<IAmazonSQS>(sqs);
                    services.AddSingleton(mongo);
                }
            )
            .Build();
        sqs.ObserveMutation = false;
        try
        {
            await Publish(
                sqs,
                queue,
                body,
                [],
                command.IdempotencyKey,
                digest.CreateRecipientLane(command.EmailAddress),
                token
            );
            await host.StartAsync(token);
            await WaitForAsync(async () => Assert.Equal(0, await Count(sqs, queue, token)));
            var requests = await fixture.GetFromJsonAsync<JsonElement[]>("/test/requests", token);
            Assert.DoesNotContain(
                requests!,
                request =>
                    request.GetProperty("reference").GetString() == digest.CreateNotifyReference(command.IdempotencyKey)
            );
        }
        finally
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.StopAsync(stopping.Token);
            sqs.ObserveMutation = true;
        }
    }

    private static NotificationCommand Command(string key, string recipient) =>
        new(
            1,
            key,
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            "private-type@example.com",
            recipient,
            PrivateContent,
            JsonSerializer.SerializeToElement(new { content = PrivateContent })
        );

    private static HttpRequestMessage Request(string action, string? selectionToken = null, bool oauth = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/notification-commands/dlq/{action}");
        if (action == "discard")
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { selectionToken }),
                Encoding.UTF8,
                "application/json"
            );
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            oauth
                ? GatewayOAuthToken.Create("admin")
                : "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{Secret}"))
        );

        return request;
    }

    private static Task<SendMessageResponse> Publish(
        IAmazonSQS sqs,
        string queue,
        string body,
        Dictionary<string, MessageAttributeValue> attributes,
        string deduplicationId,
        string lane,
        CancellationToken token
    ) =>
        sqs.SendMessageAsync(
            new SendMessageRequest
            {
                QueueUrl = queue,
                MessageBody = body,
                MessageAttributes = attributes,
                MessageDeduplicationId = deduplicationId,
                MessageGroupId = lane,
            },
            token
        );

    private static async Task<int> Count(IAmazonSQS sqs, string queue, CancellationToken token)
    {
        var result = await sqs.GetQueueAttributesAsync(
            new GetQueueAttributesRequest
            {
                QueueUrl = queue,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            },
            token
        );

        return int.Parse(
                result.Attributes["ApproximateNumberOfMessages"],
                System.Globalization.CultureInfo.InvariantCulture
            )
            + int.Parse(
                result.Attributes["ApproximateNumberOfMessagesNotVisible"],
                System.Globalization.CultureInfo.InvariantCulture
            );
    }

    // TEST-ONLY AWS ReceiveMessage replay adapter: Floci has no native ReceiveRequestAttemptId replay.
    // FIFO deletion, visibility, isolation and Mongo persistence are real; only the missing replay API is supplied.
    private sealed class ReplaySqsClient(IMongoDatabase database)
        : AmazonSQSClient(
            new BasicAWSCredentials("test", "test"),
            new AmazonSQSConfig { ServiceURL = "http://localhost:4566", AuthenticationRegion = "eu-west-2" }
        )
    {
        private readonly Dictionary<string, ReceiveMessageResponse> _selections = new();
        private readonly Dictionary<string, int?> _visibilityTimeouts = new();
        public bool FailNextDelete { get; set; }
        public string? ExpectedNotificationKey { get; set; }
        public string? SelectedReceipt { get; private set; }
        public int SelectedDeletes { get; private set; }
        public int MutationPublications { get; private set; }
        public bool ObserveMutation { get; set; }

        public override async Task<ReceiveMessageResponse> ReceiveMessageAsync(
            ReceiveMessageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (
                request.ReceiveRequestAttemptId is not null
                && _selections.TryGetValue(request.ReceiveRequestAttemptId, out var selected)
            )
            {
                Assert.Equal(_visibilityTimeouts[request.ReceiveRequestAttemptId], request.VisibilityTimeout);
                await base.ChangeMessageVisibilityAsync(
                    request.QueueUrl,
                    Assert.Single(selected.Messages).ReceiptHandle,
                    request.VisibilityTimeout ?? 120,
                    cancellationToken
                );

                return selected;
            }
            var response = await base.ReceiveMessageAsync(request, cancellationToken);
            if (request.ReceiveRequestAttemptId is not null && response.Messages is { Count: 1 })
            {
                _selections.Add(request.ReceiveRequestAttemptId, response);
                _visibilityTimeouts.Add(request.ReceiveRequestAttemptId, request.VisibilityTimeout);
                SelectedReceipt = response.Messages[0].ReceiptHandle;
            }

            return response;
        }

        public override Task<SendMessageResponse> SendMessageAsync(
            SendMessageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (ObserveMutation)
                MutationPublications++;

            return base.SendMessageAsync(request, cancellationToken);
        }

        public override async Task<DeleteMessageResponse> DeleteMessageAsync(
            DeleteMessageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (
                _selections.Values.Any(selection =>
                    selection.Messages.Any(message => message.ReceiptHandle == request.ReceiptHandle)
                )
            )
            {
                var record = await database
                    .GetCollection<BsonDocument>("NotificationDeliveryRecord")
                    .Find(new BsonDocument("notificationKey", ExpectedNotificationKey))
                    .SingleAsync(cancellationToken);
                Assert.Equal("delivery-abandoned", record["outcome"].AsString);
                SelectedDeletes++;
                if (FailNextDelete)
                {
                    FailNextDelete = false;
                    throw new InvalidOperationException("private-delete-failure");
                }
            }
            var response = await base.DeleteMessageAsync(request, cancellationToken);
            foreach (
                var key in _selections
                    .Where(entry => entry.Value.Messages.Any(message => message.ReceiptHandle == request.ReceiptHandle))
                    .Select(entry => entry.Key)
                    .ToArray()
            )
            {
                _selections.Remove(key);
                _visibilityTimeouts.Remove(key);
            }

            return response;
        }
    }

    private static async Task Cleanup(
        Func<CancellationToken, Task> operation,
        string resource,
        List<Exception> failures
    )
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await operation(cleanup.Token);
        }
        catch (Exception exception)
        {
            failures.Add(new InvalidOperationException($"Failed to clean up the owned {resource}.", exception));
        }
    }

    private static async Task RemoveQueue(IAmazonSQS sqs, string name, string? url, CancellationToken token)
    {
        if (url is null)
        {
            try
            {
                url = (await sqs.GetQueueUrlAsync(name, token)).QueueUrl;
            }
            catch (QueueDoesNotExistException)
            {
                // A failed/indeterminate creation has no resource to remove when SQS confirms this owned name is absent.

                return;
            }
        }
        await sqs.DeleteQueueAsync(url, token);
    }

    private sealed class AdministrationApplicationFactory(
        string dlq,
        string commandQueue,
        string databaseName,
        ReplaySqsClient sqs,
        IMongoClient mongo,
        int lifetime,
        bool lateWrite = false,
        bool oauth = false
    ) : WebApplicationFactory<Program>
    {
        public ReplaySqsClient Sqs { get; } = sqs;
        public RecordingLogs Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                // Use the genuine isolated Mongo client: the SDK's AWS mechanism registration is process-global.
                // Both in-process hosts retain the actual migrations, store and readiness at this Mongo boundary.
                services.RemoveAll<IMongoClient>();
                services.AddSingleton(mongo);
                if (lateWrite)
                {
                    services.RemoveAll<INotificationDeliveryRecordStore>();
                    services.AddSingleton<INotificationDeliveryRecordStore>(provider => new LateConfirmationStore(
                        new MongoNotificationDeliveryRecordStore(
                            mongo,
                            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MongoDbOptions>>(),
                            provider.GetRequiredService<INotificationCommandDigest>(),
                            provider.GetRequiredService<MongoMigrationReadiness>(),
                            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<NotificationCommandDeliveryOptions>>()
                        )
                    ));
                }
                services.RemoveAll<IAmazonSQS>();
                services.AddSingleton<IAmazonSQS>(Sqs);
                services.AddSingleton<ILoggerFactory>(_ =>
                    LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs))
                );
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AWS_EMF_ENABLED"] = "false",
                        ["AnalyticsEventConsumer:ProcessingEnabled"] = "false",
                        ["AnalyticsEventConsumer:QueueUrl"] = AnalyticsEventsQueueUrl,
                        ["NotificationCommandDelivery:ProcessingEnabled"] = "false",
                        ["NotificationCommandDelivery:QueueUrl"] = commandQueue,
                        ["NotificationCommandDelivery:EvidenceDigestSecret"] = "local-inspection-evidence-secret",
                        ["NotificationCommandDelivery:RecipientLaneSecret"] = "local-inspection-lane-secret",
                        ["NotificationCommandDelivery:EmailDeliveryCutoverUtc"] = "set-automatically-when-deployed",
                        ["NotificationCommandDelivery:NotifyTimeoutSeconds"] = "0",
                        ["NotificationCommandDelivery:ReceiveTimeoutSeconds"] = "0",
                        ["CommandDlqAdministration:Enabled"] = "true",
                        ["CommandDlqAdministration:QueueUrl"] = dlq,
                        ["CommandDlqAdministration:DependencyTimeoutSeconds"] = lateWrite ? "1" : "10",
                        ["CommandDlqAdministration:SelectionLifetimeSeconds"] = lifetime.ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        ),
                        ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
                        ["Mongo:DatabaseName"] = databaseName,
                        ["Notify:ApiKey"] = "set-automatically-when-deployed",
                        ["Notify:BaseAddress"] = "set-automatically-when-deployed",
                        ["Acl:Clients:admin:Type"] = oauth ? "OAuth" : "ApiKey",
                        ["Acl:Clients:admin:Secret"] = oauth ? null : Secret,
                        ["Acl:Clients:admin:Scopes:0"] = "admin",
                    }
                )
            );

            return base.CreateHost(builder);
        }
    }

    private sealed class RecordingLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public TaskCompletionSource EndpointEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void Dispose() { }

        private sealed class RecordingLogger(RecordingLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                var message = $"{formatter(state, exception)} {exception}";
                owner.Messages.Enqueue(message);
                if (
                    message.Contains("Executing endpoint", StringComparison.Ordinal)
                    && message.Contains("/admin/notification-commands/dlq", StringComparison.Ordinal)
                )
                    owner.EndpointEntered.TrySetResult();
            }
        }
    }

    // A lost/late database confirmation is simulated after the genuine Mongo operation has completed.
    private sealed class LateConfirmationStore(INotificationDeliveryRecordStore actual)
        : INotificationDeliveryRecordStore
    {
        public Task<NotificationDeliveryState> Inspect(
            NotificationCommand command,
            CancellationToken cancellationToken
        ) => actual.Inspect(command, cancellationToken);

        public Task<SuppressionClaimResult> RecordSuppression(
            NotificationCommand command,
            CancellationToken cancellationToken
        ) => actual.RecordSuppression(command, cancellationToken);

        public Task<DeliveryClaimResult> Claim(
            NotificationCommand command,
            string attemptOwner,
            int leaseDurationSeconds,
            CancellationToken cancellationToken
        ) => actual.Claim(command, attemptOwner, leaseDurationSeconds, cancellationToken);

        public Task<bool> RecordAcceptance(
            NotificationCommand command,
            string attemptOwner,
            NotifyAcceptance acceptance,
            CancellationToken cancellationToken
        ) => actual.RecordAcceptance(command, attemptOwner, acceptance, cancellationToken);

        public async Task<AbandonmentResult> RecordAbandonment(
            NotificationCommand command,
            CancellationToken cancellationToken
        )
        {
            var result = await actual.RecordAbandonment(command, cancellationToken);
            await Task.Delay(1150, TestContext.Current.CancellationToken);

            return result;
        }
    }
}
