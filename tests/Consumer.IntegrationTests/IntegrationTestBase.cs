using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests;

[Trait("Category", "IntegrationTests")]
[Collection("Integration Tests")]
public abstract class IntegrationTestBase : IAsyncLifetime
{
    protected const string AnalyticsEventsQueueUrl =
        "http://localhost:4566/000000000000/waste_obligations_notifications_analytics_events_queue";
    protected const string CommandQueueUrl =
        "http://localhost:4566/000000000000/waste_obligations_notifications_commands.fifo";
    protected const string CommandDeadLetterQueueUrl =
        "http://localhost:4566/000000000000/waste_obligations_notifications_commands_dlq.fifo";

    private static readonly Uri s_consumerBaseAddress = new("http://localhost:8085");

    public async ValueTask InitializeAsync()
    {
        using var sqsClient = CreateSqsClient();
        await sqsClient.GetQueueAttributesAsync(
            new GetQueueAttributesRequest { QueueUrl = AnalyticsEventsQueueUrl, AttributeNames = ["QueueArn"] },
            TestContext.Current.CancellationToken
        );
        await sqsClient.GetQueueAttributesAsync(
            new GetQueueAttributesRequest { QueueUrl = CommandQueueUrl, AttributeNames = ["QueueArn"] },
            TestContext.Current.CancellationToken
        );

        await WaitForAsync(async () =>
        {
            using var client = CreateClient();
            using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
        });
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        return ValueTask.CompletedTask;
    }

    protected static HttpClient CreateClient() => new() { BaseAddress = s_consumerBaseAddress };

    protected static IAmazonSQS CreateSqsClient()
    {
        var configuration = new AmazonSQSConfig
        {
            ServiceURL = "http://localhost:4566",
            AuthenticationRegion = "eu-west-2",
        };
        return new AmazonSQSClient(new BasicAWSCredentials("test", "test"), configuration);
    }

    protected static IMongoClient CreateMongoClient() => new MongoClient("mongodb://localhost:27017");

    protected static async Task WaitForAsync(Func<Task> assertion, TimeSpan? timeout = null)
    {
        using var timeoutSource = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));

        while (!timeoutSource.IsCancellationRequested)
        {
            try
            {
                await assertion();

                return;
            }
            catch (Exception) when (!timeoutSource.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeoutSource.Token);
            }
        }

        await assertion();
    }
}
