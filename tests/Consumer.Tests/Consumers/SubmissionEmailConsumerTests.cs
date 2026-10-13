using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Delivery;
using Defra.WasteObligations.Consumer.Startup;
using Defra.WasteObligations.Consumer.Submission;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.Extensions;

namespace Defra.WasteObligations.Consumer.Tests.Consumers;

public sealed class SubmissionEmailConsumerTests
{
    private const string OrganisationId = "9a47d78a-1983-4733-af0f-413a245565a2";
    private const string DeclarationId = "65f1f6570bb08052a8a27b01";
    private const string EventId = "01JZ8RXBMTY2K15SJB3PCFN3D5";
    private const string MessageId = "2d1af1b5-3639-4892-895a-92b8dab7f1e3";
    private const string Recipient = " ORIGINAL@example.com ";
    private const string UserName = "Distinctive Original Submitter";
    private const string PrivateFailure = "private-dependency-text recipient@example.com secret-credential";
    private const string SubmissionKey = "submission:9a47d78a-1983-4733-af0f-413a245565a2:65f1f6570bb08052a8a27b01";
    private const string DirectEnglish = "11111111-1111-4111-8111-111111111111";
    private const string DirectWelsh = "22222222-2222-4222-8222-222222222222";
    private const string SchemeEnglish = "33333333-3333-4333-8333-333333333333";
    private const string SchemeWelsh = "44444444-4444-4444-8444-444444444444";

    private static readonly string s_readCredential = "réad-fixture-" + Guid.NewGuid().ToString("N");

    [Theory]
    [InlineData("DirectProducer", "GB-ENG", "Cancelled", "submitted-direct-en", DirectEnglish, "The ", "the ")]
    [InlineData("DirectProducer", "GB-WLS", "Accepted", "submitted-direct-cy", DirectWelsh, "", "")]
    [InlineData("ComplianceScheme", "GB-ENG", "Accepted", "submitted-scheme-en", SchemeEnglish, "The ", "the ")]
    [InlineData("ComplianceScheme", "GB-WLS", "Cancelled", "submitted-scheme-cy", SchemeWelsh, "", "")]
    public async Task WhenSubmissionArrives_ShouldPublishOriginalSubmissionUsingConfiguredVariantBeforeAcknowledgement(
        string registrationType,
        string country,
        string status,
        string notificationType,
        string template,
        string leading,
        string inline
    )
    {
        var requests = new List<string>();
        using var harness = new Harness(EventBody(version: 1), (request, _) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            var authentication = Assert.IsType<AuthenticationHeaderValue>(request.Headers.Authorization);
            Assert.Equal("Basic", authentication.Scheme);
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes($"reader:{s_readCredential}")), authentication.Parameter);

            return Task.FromResult(Response(DeclarationBody(registrationType, country, status, version: 2)));
        });
        harness.Sqs.Configure().DeleteMessageAsync("http://localhost/analytics", "receipt", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Assert.Single(harness.Publications);
                harness.Deleted.TrySetResult();

                return new DeleteMessageResponse();
            });

        await harness.Start();
        await harness.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await harness.Stop();

        Assert.Equal($"/organisations/{OrganisationId}/compliance-declarations/{DeclarationId}", Assert.Single(requests));
        var publication = Assert.Single(harness.Publications);
        Assert.Equal(SubmissionKey, publication.MessageDeduplicationId);
        var command = JsonSerializer.Deserialize<NotificationCommand>(
            publication.MessageBody,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;
        Assert.Equal(SubmissionKey, command.IdempotencyKey);
        Assert.Equal("original@example.com", command.EmailAddress);
        Assert.Equal(template, command.TemplateId);
        Assert.Equal(notificationType, command.NotificationType);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero).AddMilliseconds(123), command.ActionOccurredAtUtc);
        Assert.Equal(5, command.Personalisation.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Number, command.Personalisation.GetProperty("obligationYear").ValueKind);
        Assert.Equal(2025, command.Personalisation.GetProperty("obligationYear").GetInt32());
        Assert.Equal(leading + "stored regulator", command.Personalisation.GetProperty("regulatorLeading").GetString());
        Assert.Equal(inline + "stored regulator", command.Personalisation.GetProperty("regulatorInline").GetString());
        Assert.Equal("regulator@example.com", command.Personalisation.GetProperty("regulatorEmail").GetString());
        Assert.Equal(UserName, command.Personalisation.GetProperty("user").GetString());
        var information = Assert.Single(
            harness.Logger.Entries,
            entry => entry.Text.StartsWith("Published submitted email command", StringComparison.Ordinal)
        );
        Assert.Contains(EventId, information.State);
        Assert.Contains("cdec_" + DeclarationId, information.State);
        Assert.Contains(template, information.State);
        Assert.Contains(notificationType, information.State);
        Assert.Contains(harness.Digest.CreateNotifyReference(SubmissionKey), information.State);
        AssertSafe(harness.Logger);
    }

    [Theory]
    [InlineData("older", "older-declaration-version")]
    [InlineData("wrong-declaration", "declaration-identity-mismatch")]
    [InlineData("wrong-organisation", "declaration-identity-mismatch")]
    [InlineData("missing-audit", "invalid-declaration")]
    [InlineData("missing-email", "invalid-declaration")]
    [InlineData("missing-name", "invalid-declaration")]
    [InlineData("missing-regulator", "invalid-declaration")]
    [InlineData("missing-regulator-email", "invalid-declaration")]
    [InlineData("missing-registration", "invalid-declaration")]
    [InlineData("invalid-year", "invalid-declaration")]
    [InlineData("invalid-timestamp", "invalid-declaration")]
    [InlineData("invalid-version", "invalid-data")]
    [InlineData("not-found", "declaration-not-found")]
    public async Task WhenDeclarationIsUnusable_ShouldRetainAnalyticsWithoutPublication(string scenario, string reason)
    {
        var body = DeclarationBody();
        body = scenario switch
        {
            "older" => body.Replace("\"version\": 3", "\"version\": 1", StringComparison.Ordinal),
            "wrong-declaration" => body.Replace(DeclarationId, "65f1f6570bb08052a8a27b02", StringComparison.Ordinal),
            "wrong-organisation" => body.Replace(OrganisationId, "9a47d78a-1983-4733-af0f-413a245565a3", StringComparison.Ordinal),
            "missing-audit" => body.Replace("Submitted", "Draft", StringComparison.Ordinal),
            "missing-email" => body.Replace(Recipient, "", StringComparison.Ordinal),
            "missing-name" => body.Replace(UserName, "", StringComparison.Ordinal),
            "missing-regulator" => body.Replace("stored regulator", "", StringComparison.Ordinal),
            "missing-regulator-email" => body.Replace("regulator@example.com", "", StringComparison.Ordinal),
            "missing-registration" => body.Replace("DirectProducer", "", StringComparison.Ordinal),
            "invalid-year" => body.Replace("2025", "0", StringComparison.Ordinal),
            "invalid-timestamp" => body.Replace("2026-09-28T10:00:00.123Z", "2026-09-28T10:00:00", StringComparison.Ordinal),
            "invalid-version" => body.Replace("\"version\": 3", "\"version\": \"private-value\"", StringComparison.Ordinal),
            _ => body,
        };
        using var harness = new Harness(
            EventBody(),
            (_, _) => Task.FromResult(Response(body, scenario == "not-found" ? HttpStatusCode.NotFound : HttpStatusCode.OK))
        );

        await harness.Start();
        await harness.Logger.Warning.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await harness.Stop();

        Assert.Empty(harness.Publications);
        Assert.False(harness.Deleted.Task.IsCompleted);
        var warning = Assert.Single(harness.Logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(reason, warning.State);
        Assert.Contains(MessageId, warning.State);
        Assert.Contains(EventId, warning.State);
        Assert.Contains("not acknowledged", warning.Text);
        AssertSafe(harness.Logger);
    }

    [Theory]
    [InlineData("compliance_declaration_v1.3", true)]
    [InlineData("unsupported-schema", false)]
    public async Task WhenQualifyingEventHasInvalidSchemaOrVersion_ShouldNotReadOrPublish(
        string schema,
        bool malformedVersion
    )
    {
        var reads = 0;
        var body = EventBody().Replace("compliance_declaration_v1.3", schema, StringComparison.Ordinal);
        if (malformedVersion)
            body = body.Replace("\"version\": 2", "\"version\": \"bad-version\"", StringComparison.Ordinal);
        using var harness = new Harness(body, (_, _) =>
        {
            Interlocked.Increment(ref reads);

            return Task.FromResult(Response(DeclarationBody()));
        });

        await harness.Start();
        await harness.Logger.Warning.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await harness.Stop();

        Assert.Equal(0, reads);
        Assert.Empty(harness.Publications);
        Assert.False(harness.Deleted.Task.IsCompleted);
        AssertSafe(harness.Logger);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenPublicationOrAcknowledgementIsUnconfirmed_ShouldLogAccurateRetryWarning(bool published)
    {
        using var harness = new Harness(EventBody(), (_, _) => Task.FromResult(Response(DeclarationBody())));
        if (published)
            harness.Sqs.Configure().DeleteMessageAsync("http://localhost/analytics", "receipt", Arg.Any<CancellationToken>())
                .Returns(Task.FromException<DeleteMessageResponse>(new InvalidOperationException(PrivateFailure)));
        else
            harness.Sqs.Configure().SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(new SendMessageResponse { HttpStatusCode = HttpStatusCode.ServiceUnavailable });

        await harness.Start();
        await harness.Logger.Warning.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await harness.Stop();

        var warning = Assert.Single(harness.Logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(published ? "acknowledgement-unconfirmed" : "publication-unconfirmed", warning.State);
        Assert.Contains(published ? "publication confirmed but acknowledgement unconfirmed" : "not acknowledged", warning.Text);
        Assert.Equal(
            published,
            harness.Logger.Entries.Any(entry => entry.Text.StartsWith("Published submitted email command", StringComparison.Ordinal))
        );
        Assert.False(harness.Deleted.Task.IsCompleted);
        AssertSafe(harness.Logger);
    }

    [Fact]
    public async Task WhenDeclarationReadTimesOut_ShouldBackOffThenPublishAndAcknowledgeLaterEvent()
    {
        var reads = 0;
        var timedOutAt = 0L;
        var backoff = TimeSpan.Zero;
        using var harness = new Harness(EventBody(), async (_, token) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    timedOutAt = Stopwatch.GetTimestamp();
                }
            }
            backoff = Stopwatch.GetElapsedTime(timedOutAt);

            return Response(DeclarationBody());
        }, readTimeoutSeconds: 1);
        var receives = 0;
        harness.Sqs.Configure().ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (Interlocked.Increment(ref receives) <= 2)
                {
                    return new ReceiveMessageResponse { Messages = [CreateMessage(EventBody())] };
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());

                return new ReceiveMessageResponse();
            });

        await harness.Start();
        await harness.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await harness.Stop();

        Assert.Equal(2, reads);
        Assert.True(backoff >= TimeSpan.FromMilliseconds(800));
        Assert.Single(harness.Publications);
        Assert.Contains(harness.Logger.Entries, entry => entry.Level == LogLevel.Warning && entry.State.Contains("dependency-timeout", StringComparison.Ordinal));
        AssertSafe(harness.Logger);
    }

    [Fact]
    public async Task WhenHostStopsDuringDeclarationRead_ShouldNotPublishAcknowledgeOrWarn()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = new Harness(EventBody(), async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);

            return Response(DeclarationBody());
        });

        await harness.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await harness.Stop();

        Assert.Empty(harness.Publications);
        Assert.False(harness.Deleted.Task.IsCompleted);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level is LogLevel.Warning or LogLevel.Error);
        AssertSafe(harness.Logger);
    }

    [Fact]
    public async Task WhenDeclarationResponseArrivesAfterReadDeadline_ShouldRejectItBeforePublication()
    {
        using var harness = new Harness(
            EventBody(),
            async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken);

                return Response(DeclarationBody());
            },
            readTimeoutSeconds: 1
        );

        await harness.Start();
        await harness.Logger.Warning.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await harness.Stop();

        Assert.Empty(harness.Publications);
        Assert.False(harness.Deleted.Task.IsCompleted);
        Assert.Contains(harness.Logger.Entries, entry => entry.State.Contains("dependency-timeout", StringComparison.Ordinal));
        AssertSafe(harness.Logger);
    }

    [Fact]
    public async Task WhenDeclarationResponseBodyStalls_ShouldCancelBufferingAndRetainAnalytics()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var harness = new Harness(
            EventBody(),
            (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StalledContent(cancelled),
                }
            ),
            readTimeoutSeconds: 1
        );

        await harness.Start();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await harness.Logger.Warning.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await harness.Stop();

        Assert.Empty(harness.Publications);
        Assert.False(harness.Deleted.Task.IsCompleted);
        Assert.Contains(harness.Logger.Entries, entry => entry.State.Contains("dependency-timeout", StringComparison.Ordinal));
        AssertSafe(harness.Logger);
    }

    [Fact]
    public async Task WhenReadDependencyThrowsPrivateText_ShouldLogOnlyFixedReasonAndCorrelation()
    {
        using var harness = new Harness(EventBody(), (_, _) => throw new InvalidOperationException(PrivateFailure));

        await harness.Start();
        await harness.Logger.Warning.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await harness.Stop();

        Assert.Empty(harness.Publications);
        Assert.False(harness.Deleted.Task.IsCompleted);
        var warning = Assert.Single(harness.Logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("dependency-failed", warning.State);
        Assert.Contains("declaration-read", warning.State);
        AssertSafe(harness.Logger);
    }

    [Fact]
    public async Task WhenReplayHasNewTransportAndEntityVersion_ShouldPreserveCommandIdentityAndImmutableValues()
    {
        using var harness = new Harness(EventBody(), (_, _) => Task.FromResult(Response(DeclarationBody())));
        var receives = 0;
        var acknowledged = 0;
        harness.Sqs.Configure().ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var count = Interlocked.Increment(ref receives);
                if (count <= 2)
                {
                    var message = CreateMessage(EventBody(count == 1 ? 2 : 3));
                    message.MessageId = count == 1 ? MessageId : "607e29a1-7b61-400d-9285-5595954dd918";

                    return new ReceiveMessageResponse { Messages = [message] };
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());

                return new ReceiveMessageResponse();
            });
        harness.Sqs.Configure().DeleteMessageAsync("http://localhost/analytics", "receipt", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref acknowledged) == 2)
                    harness.Deleted.TrySetResult();

                return new DeleteMessageResponse();
            });

        await harness.Start();
        await harness.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await harness.Stop();

        Assert.Equal(2, harness.Publications.Count);
        Assert.All(harness.Publications, request => Assert.Equal(SubmissionKey, request.MessageDeduplicationId));
        Assert.Equal(harness.Publications[0].MessageBody, harness.Publications[1].MessageBody);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenIdentifiersOrEncodingContainPrivateText_ShouldUseOnlySafeTransportCorrelation(
        bool unsupportedEncoding
    )
    {
        using var harness = new Harness("{}", (_, _) => throw new InvalidOperationException(PrivateFailure));
        var message = CreateMessage(EventBody().Replace(EventId, PrivateFailure, StringComparison.Ordinal)
            .Replace("cdec_" + DeclarationId, PrivateFailure, StringComparison.Ordinal));
        if (unsupportedEncoding)
        {
            message.MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["Content-Encoding"] = new() { StringValue = PrivateFailure },
            };
        }
        harness.Sqs.Configure().ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiveMessageResponse { Messages = [message] });

        await harness.Start();
        await harness.Logger.Warning.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await harness.Stop();

        var warning = Assert.Single(harness.Logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(unsupportedEncoding ? "unsupported-encoding" : "invalid-event", warning.State);
        Assert.Contains(MessageId, warning.State);
        AssertSafe(harness.Logger);
    }

    private static void AssertSafe(RecordingLogger logger)
    {
        Assert.All(logger.Entries, entry =>
        {
            Assert.Null(entry.Exception);
            foreach (var privateValue in new[]
            {
                Recipient.Trim(),
                UserName,
                "regulator@example.com",
                "later@example.com",
                "Later Actor",
                s_readCredential,
                SubmissionKey,
                PrivateFailure,
            })
            {
                Assert.DoesNotContain(privateValue, entry.State, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(privateValue, entry.Text, StringComparison.OrdinalIgnoreCase);
            }
        });
    }

    private static Message CreateMessage(string body) => new()
    {
        MessageId = MessageId,
        ReceiptHandle = "receipt",
        Body = body,
    };

    private static string EventBody(int version = 2) => $$"""
        { "eventId": "{{EventId}}", "entityId": "cdec_{{DeclarationId}}", "entity": "compliance_declaration",
          "eventType": "submission.created", "schemaVersion": "compliance_declaration_v1.3", "version": {{version}},
          "after": { "organisation": { "id": "{{OrganisationId}}", "businessCountry": "GB-SCT", "regulator": "current organisation" } } }
        """;

    private static string DeclarationBody(
        string registrationType = "DirectProducer",
        string country = "GB-ENG",
        string status = "Cancelled",
        int version = 3
    ) => $$"""
        { "id": "{{DeclarationId}}", "version": {{version}}, "status": "{{status}}", "obligationYear": 2025,
          "organisation": { "id": "{{OrganisationId}}", "registrationType": "{{registrationType}}",
            "businessCountry": "{{country}}", "regulator": "stored regulator", "regulatorEmail": "regulator@example.com" },
          "audit": [ { "action": "Submitted", "timestamp": "2026-09-28T10:00:00.123Z",
            "user": { "email": "{{Recipient}}", "name": "{{UserName}}", "locale": "cy" } },
            { "action": "Submitted", "timestamp": "2026-09-29T10:00:00Z", "user": { "email": "later@example.com", "name": "Later Actor" } },
            { "action": "Cancelled", "timestamp": "2026-09-30T10:00:00Z", "user": { "email": "later@example.com", "name": "Later Actor" } } ] }
        """;

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly AnalyticsEventConsumer _consumer;
        public IAmazonSQS Sqs { get; } = Substitute.For<IAmazonSQS>();
        public List<SendMessageRequest> Publications { get; } = [];
        public RecordingLogger Logger { get; } = new();
        public TaskCompletionSource Deleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NotificationCommandDigest Digest { get; }

        public Harness(
            string body,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> read,
            int readTimeoutSeconds = 10
        )
        {
            var receiveCount = 0;
            Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    if (Interlocked.Increment(ref receiveCount) == 1)
                    {
                        return new ReceiveMessageResponse { Messages = [CreateMessage(body)] };
                    }
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());

                    return new ReceiveMessageResponse();
                });
            Sqs.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(new SendMessageResponse { HttpStatusCode = HttpStatusCode.OK })
                .AndDoes(call => Publications.Add(call.Arg<SendMessageRequest>()));
            Sqs.DeleteMessageAsync("http://localhost/analytics", "receipt", Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Deleted.TrySetResult();

                    return new DeleteMessageResponse();
                });
            var deliveryOptions = Options.Create(
                new NotificationCommandDeliveryOptions
                {
                    QueueUrl = "http://localhost/commands.fifo",
                    EvidenceDigestSecret = "test-evidence-secret",
                    RecipientLaneSecret = "test-lane-secret",
                }
            );
            Digest = new NotificationCommandDigest(deliveryOptions);
            var analyticsOptions = Options.Create(
                new AnalyticsEventConsumerOptions
                {
                    QueueUrl = "http://localhost/analytics",
                    ProcessingEnabled = true,
                    WaitTimeSeconds = 0,
                    PollIntervalSeconds = 1,
                    ReadTimeoutSeconds = readTimeoutSeconds,
                }
            );
            var submissionOptions = Options.Create(
                new SubmissionEmailOptions
                {
                    ApiBaseAddress = "http://declarations",
                    ClientId = "reader",
                    ClientSecret = s_readCredential,
                    DirectEnglishTemplateId = DirectEnglish,
                    DirectWelshTemplateId = DirectWelsh,
                    SchemeEnglishTemplateId = SchemeEnglish,
                    SchemeWelshTemplateId = SchemeWelsh,
                }
            );
            _httpClient = new HttpClient(new DeclarationHandler(read)) { BaseAddress = new Uri("http://declarations") };
            var startup = new ApplicationStartup();
            startup.MarkStarted();
            _consumer = new AnalyticsEventConsumer(
                Sqs,
                analyticsOptions,
                Logger,
                startup,
                new DeclarationClient(_httpClient, submissionOptions),
                new SubmissionCommandMapper(submissionOptions),
                new NotificationCommandPublisher(Sqs, deliveryOptions, Digest),
                Digest,
                new AnalyticsProcessingBudget(analyticsOptions)
            );
        }

        public Task Start() => _consumer.StartAsync(TestContext.Current.CancellationToken);

        public Task Stop() => _consumer.StopAsync(TestContext.Current.CancellationToken);

        public void Dispose()
        {
            _consumer.Dispose();
            _httpClient.Dispose();
        }
    }

    private sealed class DeclarationHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> read
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => read(request, cancellationToken);
    }

    private sealed class StalledContent(TaskCompletionSource cancelled) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Buffering must carry cancellation.");

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken
        )
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                cancelled.TrySetResult();
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }

    private sealed record LogEntry(LogLevel Level, string Text, string State, Exception? Exception);

    private sealed class RecordingLogger : ILogger<AnalyticsEventConsumer>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public TaskCompletionSource Warning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            var fields = state is IEnumerable<KeyValuePair<string, object?>> values
                ? string.Join(";", values.Select(value => value.Key + "=" + value.Value)) : "";
            Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), fields, exception));
            if (logLevel == LogLevel.Warning)
                Warning.TrySetResult();
        }
    }
}
