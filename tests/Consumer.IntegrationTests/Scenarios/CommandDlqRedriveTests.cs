using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
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

public sealed class CommandDlqRedriveTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions s_indentedJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private const string Secret = "local-test-admin:secret";
    private const string PrivateContent = "private-redrive-personalisation-template";

    [Theory]
    [InlineData("confirmed", false)]
    [InlineData("confirmed", true)]
    [InlineData("delete-failure", false)]
    [InlineData("indeterminate-send", false)]
    public async Task WhenSelectionIsRedrivenAcrossHosts_ShouldRecoverInsideDeduplicationWindowAndPreserveOtherMessagesAndEvidence(
        string failure,
        bool oauth
    )
    {
        using var sqs = new ReplaySqsClient();
        using var mongo = CreateMongoClient();
        var databaseName = $"notifications_redrive_{Guid.NewGuid():N}";
        var database = mongo.GetDatabase(databaseName);
        var queueName = $"redrive_dlq_{Guid.NewGuid():N}.fifo";
        var destinationName = $"redrive_destination_{Guid.NewGuid():N}.fifo";
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
            await WaitForAsync(async () =>
            {
                using var health = await firstClient.GetAsync("/health", token);
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            });
            var command = Command("private-original-key@example.com", "private-recipient@example.com");
            var digest = first.Services.GetRequiredService<INotificationCommandDigest>();
            var body = JsonSerializer.Serialize(command, s_indentedJsonOptions);
            using var bytes = new MemoryStream();
            await using (var gzip = new GZipStream(bytes, CompressionMode.Compress, leaveOpen: true))
                await gzip.WriteAsync(Encoding.UTF8.GetBytes(body), token);
            var encoded = Convert.ToBase64String(bytes.ToArray());
            var encoding = new Dictionary<string, MessageAttributeValue>
            {
                ["Content-Encoding"] = new() { DataType = "String", StringValue = "gzip+base64" },
            };
            // The original copy is consumed, but SQS still remembers its normal command-key deduplication ID.
            await Publish(
                sqs,
                destination.QueueUrl,
                encoded,
                encoding,
                command.IdempotencyKey,
                digest.CreateRecipientLane(command.EmailAddress),
                token
            );
            var original = Assert.Single(
                (
                    await sqs.ReceiveMessageAsync(
                        new ReceiveMessageRequest
                        {
                            QueueUrl = destination.QueueUrl,
                            MaxNumberOfMessages = 1,
                            WaitTimeSeconds = 0,
                        },
                        token
                    )
                ).Messages
            );
            await sqs.DeleteMessageAsync(destination.QueueUrl, original.ReceiptHandle, token);
            Assert.Equal(0, await Count(sqs, destination.QueueUrl, token));
            var selected = await Publish(
                sqs,
                queue.QueueUrl,
                encoded,
                encoding,
                command.IdempotencyKey,
                digest.CreateRecipientLane(command.EmailAddress),
                token
            );
            var store = first.Services.GetRequiredService<INotificationDeliveryRecordStore>();
            Assert.Equal(DeliveryClaimResult.Claimed, await store.Claim(command, "real-active-attempt", 60, token));
            var records = database.GetCollection<BsonDocument>("NotificationDeliveryRecord");
            var before = await records.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(token);
            using var inspectionRequest = Request("inspect", oauth: oauth);
            using var inspected = await firstClient.SendAsync(inspectionRequest, token);
            Assert.Equal(HttpStatusCode.OK, inspected.StatusCode);
            using var inspection = JsonDocument.Parse(await inspected.Content.ReadAsStringAsync(token));
            Assert.Equal("active-claim", inspection.RootElement.GetProperty("failureClassification").GetString());
            var selectionToken = inspection.RootElement.GetProperty("selectionToken").GetString();
            var selection = first.Services.GetRequiredService<CommandDlqSelectionTokens>().Validate(selectionToken);
            Assert.NotNull(selection);
            Assert.Equal(selected.MessageId, selection.MessageId);
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
            await using var second = new AdministrationApplicationFactory(
                queue.QueueUrl,
                destination.QueueUrl,
                databaseName,
                sqs,
                mongo,
                60,
                oauth: oauth
            );
            using var secondClient = second.CreateClient();
            await WaitForAsync(async () =>
            {
                using var health = await secondClient.GetAsync("/health", token);
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            });
            sqs.DestinationUrl = destination.QueueUrl;
            sqs.FailNextDelete = failure == "delete-failure";
            sqs.FailNextSendAfterPublication = failure == "indeterminate-send";
            using var redriveRequest = Request("redrive", selectionToken, oauth: oauth);
            using var redriven = await secondClient.SendAsync(redriveRequest, token);
            Assert.Equal(
                failure == "confirmed" ? HttpStatusCode.NoContent : HttpStatusCode.ServiceUnavailable,
                redriven.StatusCode
            );
            Assert.Equal(1, await Count(sqs, destination.QueueUrl, token));
            Assert.Equal(failure == "confirmed" ? 1 : 2, await Count(sqs, queue.QueueUrl, token));
            if (failure != "confirmed")
            {
                using var retryRequest = Request("redrive", selectionToken, oauth: oauth);
                using var retried = await firstClient.SendAsync(retryRequest, token);
                Assert.Equal(HttpStatusCode.NoContent, retried.StatusCode);
                Assert.Equal(1, await Count(sqs, destination.QueueUrl, token));
                Assert.Equal(1, await Count(sqs, queue.QueueUrl, token));
                Assert.Equal(2, sqs.RecoveryPublications.Count);
                Assert.Single(sqs.RecoveryPublications.Select(publication => publication.DeduplicationId).Distinct());
                Assert.Single(sqs.RecoveryPublications.Select(publication => publication.MessageId).Distinct());
            }
            var recovered = Assert.Single(
                (
                    await sqs.ReceiveMessageAsync(
                        new ReceiveMessageRequest
                        {
                            QueueUrl = destination.QueueUrl,
                            MaxNumberOfMessages = 1,
                            WaitTimeSeconds = 0,
                            MessageAttributeNames = ["All"],
                            MessageSystemAttributeNames = ["MessageGroupId"],
                        },
                        token
                    )
                ).Messages
            );
            Assert.Equal(encoded, recovered.Body);
            Assert.Equal("gzip+base64", recovered.MessageAttributes["Content-Encoding"].StringValue);
            Assert.Equal(
                JsonSerializer.Serialize(command),
                JsonSerializer.Serialize(NotificationCommandMessageReader.Read(recovered))
            );
            Assert.Equal(digest.CreateRecipientLane(command.EmailAddress), recovered.Attributes["MessageGroupId"]);
            Assert.NotEqual(
                command.IdempotencyKey,
                Assert.Single(sqs.RecoveryPublications.Select(publication => publication.DeduplicationId).Distinct())
            );
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
            Assert.Equal(before, await records.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(token));
            Assert.Equal(selection.ReceiveRequestAttemptId, sqs.ReplayedAttemptId);
            foreach (
                var value in new[]
                {
                    command.IdempotencyKey,
                    command.EmailAddress,
                    command.NotificationType,
                    PrivateContent,
                    selected.MessageId,
                    selectionToken!,
                    Secret,
                    sqs.SelectedReceipt!,
                }
            )
                Assert.All(
                    first.Logs.Messages.Concat(second.Logs.Messages),
                    log => Assert.DoesNotContain(value, log, StringComparison.Ordinal)
                );
        }
        catch (Exception exception)
        {
            testFailure = exception;
        }
        finally
        {
            await Cleanup(cleanup => RemoveQueue(sqs, queueName, queue?.QueueUrl, cleanup), "redrive DLQ", failures);
            await Cleanup(
                cleanup => RemoveQueue(sqs, destinationName, destination?.QueueUrl, cleanup),
                "redrive command queue",
                failures
            );
            await Cleanup(cleanup => mongo.DropDatabaseAsync(databaseName, cleanup), "redrive database", failures);
        }
        if (failures.Count > 0)
        {
            if (testFailure is not null)
                failures.Insert(0, testFailure);
            throw new AggregateException("Owned redrive resources could not all be cleaned up.", failures);
        }
        if (testFailure is not null)
            ExceptionDispatchInfo.Capture(testFailure).Throw();
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
        if (action == "redrive")
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
    // The missing replay API behavior is controlled here; FIFO publication, deduplication, deletion,
    // visibility, queue isolation and Mongo migrations/evidence all use the actual local dependencies.
    private sealed class ReplaySqsClient()
        : AmazonSQSClient(
            new BasicAWSCredentials("test", "test"),
            new AmazonSQSConfig { ServiceURL = "http://localhost:4566", AuthenticationRegion = "eu-west-2" }
        )
    {
        private readonly Dictionary<string, ReceiveMessageResponse> _selections = new();
        private readonly Dictionary<string, int?> _visibilityTimeouts = new();
        public string? DestinationUrl { get; set; }
        public bool FailNextDelete { get; set; }
        public bool FailNextSendAfterPublication { get; set; }
        public List<(string DeduplicationId, string MessageId)> RecoveryPublications { get; } = [];
        public string? ReplayedAttemptId { get; private set; }
        public string? SelectedReceipt { get; private set; }

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
                ReplayedAttemptId = request.ReceiveRequestAttemptId;
                // This real visibility update supplies the visibility reset of the emulated atomic replay API.
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

        public override async Task<SendMessageResponse> SendMessageAsync(
            SendMessageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var response = await base.SendMessageAsync(request, cancellationToken);
            if (request.QueueUrl == DestinationUrl)
            {
                RecoveryPublications.Add((request.MessageDeduplicationId, response.MessageId));
                if (FailNextSendAfterPublication)
                {
                    FailNextSendAfterPublication = false;
                    throw new InvalidOperationException("private-indeterminate-send");
                }
            }

            return response;
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
                Assert.NotEmpty(RecoveryPublications);
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
                        ["CommandDlqAdministration:QueueUrl"] = dlq,
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
}
