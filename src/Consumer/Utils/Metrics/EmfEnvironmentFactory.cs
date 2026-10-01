using System.Diagnostics;
using Amazon.CloudWatch.EMF.Environment;
using Microsoft.Extensions.Options;
using Environments = Amazon.CloudWatch.EMF.Environment.Environments;
using SdkConfiguration = Amazon.CloudWatch.EMF.Config.Configuration;

namespace Defra.WasteObligations.Consumer.Utils.Metrics;

public sealed class EmfEnvironmentFactory(
    IOptions<EmfOptions> options,
    IHttpClientFactory clients,
    EmfDiagnosticLoggerFactory loggerFactory
) : IEmfEnvironmentFactory
{
    public const string MetadataClientName = "EmfMetadata";

    public IEnvironment Create(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var environment = Enum.TryParse<Environments>(settings.Environment, out var selected)
            ? selected
            : Environments.Unknown;
        var configuration = new SdkConfiguration(
            settings.ServiceName ?? Metrics.ServiceName,
            settings.ServiceType,
            settings.LogGroupName,
            settings.LogStreamName,
            settings.AgentEndpoint,
            settings.AgentBufferSize,
            environment
        );
        using var client = clients.CreateClient(MetadataClientName);
        var fetcher = new EmfResourceFetcher(client, cancellationToken);
        var startedAt = Stopwatch.GetTimestamp();
        var result = environment switch
        {
            Environments.Local => new LocalEnvironment(configuration, loggerFactory),
            Environments.Lambda => new LambdaEnvironment(configuration, loggerFactory),
            Environments.Agent => new DefaultEnvironment(configuration, loggerFactory),
            Environments.ECS => new ECSEnvironment(configuration, fetcher, loggerFactory),
            Environments.EC2 => new EC2Environment(configuration, fetcher, loggerFactory),
            _ => ProbeEnvironment(),
        };
        cancellationToken.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(startedAt) >= TimeSpan.FromSeconds(8))
            throw new TimeoutException("EMF environment resolution exceeded its startup budget.");

        return result;

        IEnvironment ProbeEnvironment()
        {
            IEnvironment[] candidates =
            [
                new LambdaEnvironment(configuration, loggerFactory),
                new ECSEnvironment(configuration, fetcher, loggerFactory),
                new EC2Environment(configuration, fetcher, loggerFactory),
            ];
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.Probe())
                    return candidate;
            }

            return new DefaultEnvironment(configuration, loggerFactory);
        }
    }
}
