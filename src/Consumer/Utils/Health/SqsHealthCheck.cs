using System.Diagnostics.CodeAnalysis;
using System.Net;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Defra.WasteObligations.Consumer.Utils.Health;

[ExcludeFromCodeCoverage]
public sealed class SqsHealthCheck(IAmazonSQS sqsClient, string queueUrl) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var response = await sqsClient.GetQueueAttributesAsync(
                new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] },
                cancellationToken
            );

            if (response.HttpStatusCode is not HttpStatusCode.OK)
            {
                throw new InvalidOperationException($"Unexpected HTTP status code: {response.HttpStatusCode}");
            }

            return HealthCheckResult.Healthy($"Connected to AWS queue: {queueUrl}");
        }
        catch (Exception exception)
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                exception: new Exception($"Failed to connect to AWS queue: {queueUrl}", exception)
            );
        }
    }
}
