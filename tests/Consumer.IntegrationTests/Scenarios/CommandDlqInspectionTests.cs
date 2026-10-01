using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
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

public sealed class CommandDlqInspectionTests : IntegrationTestBase
{
    private const string Route = "/admin/notification-commands/dlq/inspect";
    private const string Secret = "local-test-admin:secret";

    [Fact]
    public async Task WhenSendingIsPaused_ShouldWaitForRealMigrationReadinessAndInspectOnlyOneRealFifoMessage()
    {
        using var sqs = CreateSqsClient();
        using var mongo = CreateMongoClient();
        var databaseName = $"notifications_inspection_{Guid.NewGuid():N}";
        var database = mongo.GetDatabase(databaseName);
        var token = TestContext.Current.CancellationToken;
        var queueName = $"inspection_{Guid.NewGuid():N}.fifo";
        var destinationName = $"inspection_destination_{Guid.NewGuid():N}.fifo";
        CreateQueueResponse? queue = null;
        CreateQueueResponse? destination = null;
        var lease = new MongoMigrationLeaseService(database, TimeProvider.System);
        Exception? testFailure = null;
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
            Assert.True(await lease.TryAcquire(TimeSpan.FromSeconds(60), token));
            var first = Command("first-private-key", "first-private-recipient@example.com");
            var second = Command("second-private-key", "second-private-recipient@example.com");
            var firstSend = await Publish(sqs, queue.QueueUrl, first, "first", token);
            var secondSend = await Publish(sqs, queue.QueueUrl, second, "second", token);
            await using var factory = new AdministrationApplicationFactory(
                queue.QueueUrl,
                destination.QueueUrl,
                databaseName
            );
            using var client = factory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            using var request = new HttpRequestMessage(HttpMethod.Post, Route);
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{Secret}"))}"
            );

            var inspecting = client.SendAsync(request, token);
            await factory.Logs.EndpointEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Equal(0, factory.Sqs.ReceiveCount);
            Assert.False(inspecting.IsCompleted);
            Assert.Equal(
                0,
                await database
                    .GetCollection<BsonDocument>("_migrations")
                    .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: token)
            );
            using var ready = await client.GetAsync("/health", token);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.Equal(0, factory.Sqs.ReceiveCount);
            await lease.Release(token);

            using var response = await inspecting;
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var selectedKey = body.RootElement.GetProperty("idempotencyKey").GetString();
            Assert.Contains(selectedKey, new[] { first.IdempotencyKey, second.IdempotencyKey });
            var selected = selectedKey == first.IdempotencyKey ? first : second;
            var other = selectedKey == first.IdempotencyKey ? second : first;
            Assert.Equal("unrecorded", body.RootElement.GetProperty("failureClassification").GetString());
            Assert.Equal(1, body.RootElement.GetProperty("receiveCount").GetInt32());
            Assert.NotEqual(JsonValueKind.Null, body.RootElement.GetProperty("sentAtUtc").ValueKind);
            Assert.StartsWith("v1:", body.RootElement.GetProperty("recipientDigest").GetString());
            Assert.Equal(1, factory.Sqs.ReceiveCount);
            var selection = factory
                .Services.GetRequiredService<Administration.CommandDlqSelectionTokens>()
                .Validate(body.RootElement.GetProperty("selectionToken").GetString());
            Assert.NotNull(selection);
            Assert.Equal(selected == first ? firstSend.MessageId : secondSend.MessageId, selection.MessageId);
            Assert.Equal(
                0,
                await database
                    .GetCollection<BsonDocument>("NotificationDeliveryRecord")
                    .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: token)
            );
            using var indexes = await database
                .GetCollection<BsonDocument>("NotificationDeliveryRecord")
                .Indexes.ListAsync(token);
            Assert.Contains(
                await indexes.ToListAsync(token),
                index =>
                    index.GetValue("name", "") == "notificationKey_unique"
                    && index.GetValue("unique", false) == BsonBoolean.True
            );
            var attributes = await sqs.GetQueueAttributesAsync(
                new GetQueueAttributesRequest
                {
                    QueueUrl = queue.QueueUrl,
                    AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
                },
                token
            );
            Assert.Equal("1", attributes.Attributes["ApproximateNumberOfMessages"]);
            Assert.Equal("1", attributes.Attributes["ApproximateNumberOfMessagesNotVisible"]);
            var remaining = await sqs.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = queue.QueueUrl,
                    MaxNumberOfMessages = 1,
                    WaitTimeSeconds = 0,
                    VisibilityTimeout = 1,
                },
                token
            );
            Assert.Equal(
                other.IdempotencyKey,
                NotificationCommandMessageReader.Read(Assert.Single(remaining.Messages)).IdempotencyKey
            );

            using var extended = await client.GetAsync("/health/all", token);
            using var health = JsonDocument.Parse(await extended.Content.ReadAsStringAsync(token));
            Assert.Equal(HttpStatusCode.OK, extended.StatusCode);
            var results = health.RootElement.GetProperty("results");
            Assert.True(results.TryGetProperty("NotificationCommandQueue", out _));
            Assert.True(results.TryGetProperty("NotificationCommandDeadLetterQueue", out _));
            Assert.True(results.TryGetProperty("NotificationDeliveryRecordStore", out _));
            Assert.False(results.TryGetProperty("Notify", out _));
            Assert.Equal(1, factory.Sqs.ReceiveCount);
            foreach (
                var value in new[]
                {
                    first.IdempotencyKey,
                    first.EmailAddress,
                    second.IdempotencyKey,
                    second.EmailAddress,
                    first.TemplateId,
                    "private-body",
                    Secret,
                }
            )
                Assert.All(
                    factory.Logs.Messages,
                    message => Assert.DoesNotContain(value, message, StringComparison.Ordinal)
                );
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            var failures = new List<Exception>();
            await Cleanup(lease.Release, "migration lease", failures);
            await Cleanup(
                cleanup => RemoveQueue(sqs, queueName, queue?.QueueUrl, cleanup),
                "inspection queue",
                failures
            );
            await Cleanup(
                cleanup => RemoveQueue(sqs, destinationName, destination?.QueueUrl, cleanup),
                "command queue",
                failures
            );
            await Cleanup(cleanup => mongo.DropDatabaseAsync(databaseName, cleanup), "inspection database", failures);
            if (failures.Count > 0)
            {
                if (testFailure is not null)
                    failures.Insert(0, testFailure);
                throw new AggregateException("Owned inspection resources could not all be cleaned up.", failures);
            }
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

    private static NotificationCommand Command(string key, string recipient) =>
        new(
            1,
            key,
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            "private-type@example.com",
            recipient,
            "private-template",
            JsonSerializer.SerializeToElement(new { body = "private-body" })
        );

    private static Task<SendMessageResponse> Publish(
        IAmazonSQS sqs,
        string queueUrl,
        NotificationCommand command,
        string group,
        CancellationToken token
    ) =>
        sqs.SendMessageAsync(
            new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = JsonSerializer.Serialize(command, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                MessageDeduplicationId = command.IdempotencyKey,
                MessageGroupId = group,
            },
            token
        );

    private sealed class AdministrationApplicationFactory(string dlq, string commandQueue, string databaseName)
        : WebApplicationFactory<Program>
    {
        public CountingSqsClient Sqs { get; } = new();
        public RecordingLogs Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
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
                        ["Mongo:DatabaseUri"] = "mongodb://localhost:27017",
                        ["Mongo:DatabaseName"] = databaseName,
                        ["Notify:ApiKey"] = "set-automatically-when-deployed",
                        ["Notify:BaseAddress"] = "set-automatically-when-deployed",
                        ["Acl:Clients:admin:Type"] = "ApiKey",
                        ["Acl:Clients:admin:Secret"] = Secret,
                        ["Acl:Clients:admin:Scopes:0"] = "admin",
                    }
                )
            );

            return base.CreateHost(builder);
        }
    }

    private sealed class CountingSqsClient()
        : AmazonSQSClient(
            new BasicAWSCredentials("test", "test"),
            new AmazonSQSConfig { ServiceURL = "http://localhost:4566", AuthenticationRegion = "eu-west-2" }
        )
    {
        private int _receives;
        public int ReceiveCount => Volatile.Read(ref _receives);

        public override Task<ReceiveMessageResponse> ReceiveMessageAsync(
            ReceiveMessageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref _receives);

            return base.ReceiveMessageAsync(request, cancellationToken);
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
                    && message.Contains(Route, StringComparison.Ordinal)
                )
                    owner.EndpointEntered.TrySetResult();
            }
        }
    }
}
