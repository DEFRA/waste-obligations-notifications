using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Defra.WasteObligations.Consumer.Delivery;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class DockerSubmittedEmailTests : IntegrationTestBase
{
    private const string TemplateId = "5f64e3bd-d454-4a45-a9c6-9409bf940d7a";

    [Fact]
    public async Task WhenSubmissionIsPublishedToTopic_ShouldSendEmailThroughDockerConsumer()
    {
        var token = TestContext.Current.CancellationToken;
        var organisationId = Guid.NewGuid();
        var declarationId = ObjectId.GenerateNewId().ToString();
        var recipient = $"docker-{Guid.NewGuid():N}@example.test";
        var digest = new NotificationCommandDigest(
            Options.Create(
                new NotificationCommandDeliveryOptions
                {
                    QueueUrl = "http://floci:4566/000000000000/submission_e2e_commands.fifo",
                    EvidenceDigestSecret = "development-only-evidence-digest-secret",
                    RecipientLaneSecret = "development-only-recipient-lane-secret",
                }
            )
        );
        var reference = digest.CreateNotifyReference($"compliance-declaration:{declarationId}:submitted:v1");
        using var mongo = CreateMongoClient();
        using var wireMock = new HttpClient { BaseAddress = new Uri("http://localhost:8087") };
        using var consumer = new HttpClient { BaseAddress = new Uri("http://localhost:8088") };

        await WaitForAsync(async () =>
        {
            using var ready = await consumer.GetAsync("/health", token);
            ready.EnsureSuccessStatusCode();
        });

        var declaration = new
        {
            id = declarationId,
            version = 1,
            obligationYear = 2026,
            organisation = new
            {
                id = organisationId,
                registrationType = "DirectProducer",
                businessCountry = "GB-ENG",
                regulator = "Stored Regulator",
                regulatorEmail = "regulator@example.test",
            },
            audit = new[]
            {
                new
                {
                    action = "Submitted",
                    timestamp = "2026-09-20T11:12:13Z",
                    user = new { email = recipient, name = "Docker Submitter" },
                },
            },
        };
        var readPath = $"/organisations/{organisationId:D}/compliance-declarations/{declarationId}";
        var basic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("isolated-docker-reader:synthetic-docker-read-secret")
        );
        var readMapping = Guid.NewGuid();
        var notifyMapping = Guid.NewGuid();

        try
        {
            await Stub(
                wireMock,
                readMapping,
                new
                {
                    Methods = new[] { "GET" },
                    Path = Exact(readPath),
                    Headers = new[]
                    {
                        new
                        {
                            Name = "Authorization",
                            Matchers = new[] { new { Name = "ExactMatcher", Pattern = $"Basic {basic}" } },
                        },
                    },
                },
                declaration,
                HttpStatusCode.OK
            );
            await Stub(
                wireMock,
                notifyMapping,
                new
                {
                    Methods = new[] { "POST" },
                    Path = Exact("/v2/notifications/email"),
                    Body = new
                    {
                        Matcher = new { Name = "JsonPathMatcher", Pattern = $"$[?(@.email_address == '{recipient}')]" },
                    },
                },
                new
                {
                    id = Guid.NewGuid(),
                    reference,
                    template = new { id = TemplateId, version = 1 },
                    content = new { body = "Controlled Docker email", subject = "Controlled submission" },
                },
                HttpStatusCode.Created
            );

            using var sns = new AmazonSimpleNotificationServiceClient(
                new BasicAWSCredentials("test", "test"),
                new AmazonSimpleNotificationServiceConfig
                {
                    ServiceURL = "http://localhost:4566",
                    AuthenticationRegion = "eu-west-2",
                }
            );
            var published = await sns.PublishAsync(
                new PublishRequest
                {
                    TopicArn = "arn:aws:sns:eu-west-2:000000000000:submission_e2e_analytics",
                    Message = JsonSerializer.Serialize(
                        new
                        {
                            eventId = Guid.NewGuid().ToString(),
                            entityId = $"cdec_{declarationId}",
                            entity = "compliance_declaration",
                            eventType = "submission.created",
                            schemaVersion = "compliance_declaration_v1.3",
                            version = 1,
                            after = new { organisation = new { id = organisationId } },
                        }
                    ),
                },
                token
            );
            Assert.Equal(HttpStatusCode.OK, published.HttpStatusCode);

            await WaitForAsync(
                async () =>
                {
                    var requests = await FindRequests(wireMock, "POST", "/v2/notifications/email");
                    var matching = requests.Where(entry => IsRecipient(entry, recipient)).ToArray();
                    var sent = Assert.Single(matching);
                    using var body = JsonDocument.Parse(sent.GetProperty("Request").GetProperty("Body").GetString()!);
                    Assert.Equal(TemplateId, body.RootElement.GetProperty("template_id").GetString());
                    Assert.Equal(reference, body.RootElement.GetProperty("reference").GetString());
                    Assert.Equal(
                        "Docker Submitter",
                        body.RootElement.GetProperty("personalisation").GetProperty("user").GetString()
                    );

                    var record = await mongo
                        .GetDatabase("submission-docker-e2e")
                        .GetCollection<BsonDocument>("NotificationDeliveryRecord")
                        .Find(Builders<BsonDocument>.Filter.Eq("notifyReference", reference))
                        .SingleAsync(token);
                    Assert.Equal("delivery-accepted", record["outcome"].AsString);
                },
                TimeSpan.FromSeconds(30)
            );

            var reads = await FindRequests(wireMock, "GET", readPath);
            Assert.Single(reads);
        }
        finally
        {
            using var deletedRead = await wireMock.DeleteAsync($"/__admin/mappings/{readMapping}", token);
            using var deletedNotify = await wireMock.DeleteAsync($"/__admin/mappings/{notifyMapping}", token);
        }
    }

    private static bool IsRecipient(JsonElement entry, string recipient)
    {
        using var body = JsonDocument.Parse(entry.GetProperty("Request").GetProperty("Body").GetString()!);

        return body.RootElement.GetProperty("email_address").GetString() == recipient;
    }

    private static object Exact(string path) =>
        new { Matchers = new[] { new { Name = "ExactMatcher", Pattern = path } } };

    private static async Task Stub(HttpClient client, Guid id, object request, object body, HttpStatusCode status)
    {
        using var result = await client.PostAsJsonAsync(
            "/__admin/mappings",
            new
            {
                Guid = id,
                Request = request,
                Response = new
                {
                    StatusCode = (int)status,
                    BodyAsJson = body,
                    Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                },
            },
            TestContext.Current.CancellationToken
        );
        result.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement[]> FindRequests(HttpClient client, string method, string path)
    {
        using var result = await client.PostAsJsonAsync(
            "/__admin/requests/find",
            new { Methods = new[] { method }, Path = Exact(path) },
            TestContext.Current.CancellationToken
        );
        result.EnsureSuccessStatusCode();
        var requests = await result.Content.ReadFromJsonAsync<JsonElement[]>(TestContext.Current.CancellationToken);

        return requests ?? [];
    }
}
