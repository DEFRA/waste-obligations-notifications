using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Defra.WasteObligations.Consumer.Commands;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Helpers;

public sealed class CancellationSqsClient : AmazonSQSClient
{
    private int _analyticsDeletions;
    private int _publicationAttempts;
    private int _commandDeletions;
    public string AnalyticsUrl { get; set; } = "";
    public string CommandUrl { get; set; } = "";
    public string? LastReceipt { get; private set; }
    public int FailPublicationAttempt { get; set; }
    public ConcurrentQueue<NotificationCommand> Confirmed { get; } = new();
    public int AnalyticsDeletions => Volatile.Read(ref _analyticsDeletions);
    public int CommandDeletions => Volatile.Read(ref _commandDeletions);
    public int PublicationAttempts => Volatile.Read(ref _publicationAttempts);

    public CancellationSqsClient()
        : base(
            new BasicAWSCredentials("test", "test"),
            new AmazonSQSConfig { ServiceURL = "http://localhost:4566", AuthenticationRegion = "eu-west-2" }
        ) { }

    public override async Task<ReceiveMessageResponse> ReceiveMessageAsync(
        ReceiveMessageRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var response = await base.ReceiveMessageAsync(request, cancellationToken);
        if (request.QueueUrl == AnalyticsUrl && response.Messages?.Count > 0)
            LastReceipt = response.Messages[0].ReceiptHandle;

        return response;
    }

    public override async Task<SendMessageResponse> SendMessageAsync(
        SendMessageRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (request.QueueUrl != CommandUrl)
            return await base.SendMessageAsync(request, cancellationToken);

        var attempt = Interlocked.Increment(ref _publicationAttempts);
        if (attempt == FailPublicationAttempt)
            return new SendMessageResponse { HttpStatusCode = HttpStatusCode.ServiceUnavailable };

        var response = await base.SendMessageAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.HttpStatusCode);
        Confirmed.Enqueue(
            JsonSerializer.Deserialize<NotificationCommand>(
                request.MessageBody,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            )!
        );

        return response;
    }

    public override async Task<DeleteMessageResponse> DeleteMessageAsync(
        string queueUrl,
        string receiptHandle,
        CancellationToken cancellationToken = default
    )
    {
        var response = await base.DeleteMessageAsync(queueUrl, receiptHandle, cancellationToken);
        if (queueUrl == AnalyticsUrl)
            Interlocked.Increment(ref _analyticsDeletions);
        else if (queueUrl == CommandUrl)
            Interlocked.Increment(ref _commandDeletions);

        return response;
    }
}
