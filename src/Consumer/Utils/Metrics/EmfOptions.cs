using System.ComponentModel.DataAnnotations;
using Amazon.CloudWatch.EMF;
using Amazon.CloudWatch.EMF.Environment;
using Environments = Amazon.CloudWatch.EMF.Environment.Environments;

namespace Defra.WasteObligations.Consumer.Utils.Metrics;

public sealed record EmfOptions
{
    [ConfigurationKeyName("AWS_EMF_ENABLED")]
    public bool Enabled { get; init; } = true;

    [ConfigurationKeyName("AWS_EMF_NAMESPACE")]
    public string? Namespace { get; init; }

    [ConfigurationKeyName("AWS_EMF_ENVIRONMENT")]
    public string? Environment { get; init; }

    [ConfigurationKeyName("AWS_EMF_SERVICE_NAME")]
    public string? ServiceName { get; init; }

    [ConfigurationKeyName("AWS_EMF_SERVICE_TYPE")]
    public string? ServiceType { get; init; }

    [ConfigurationKeyName("AWS_EMF_LOG_GROUP_NAME")]
    public string? LogGroupName { get; init; }

    [ConfigurationKeyName("AWS_EMF_LOG_STREAM_NAME")]
    public string? LogStreamName { get; init; }

    [ConfigurationKeyName("AWS_EMF_AGENT_ENDPOINT")]
    public string? AgentEndpoint { get; init; }

    [ConfigurationKeyName("AWS_EMF_AGENT_BUFFER_SIZE")]
    [Range(1, 10000)]
    public int AgentBufferSize { get; init; } = 100;

    [ConfigurationKeyName("AWS_EMF_SHUTDOWN_TIMEOUT_SECONDS")]
    [Range(1, 30)]
    public int ShutdownTimeoutSeconds { get; init; } = 5;

    public string? EffectiveNamespace =>
        string.IsNullOrWhiteSpace(Namespace) && Environment == nameof(Environments.Local)
            ? Metrics.MeterName
            : Namespace;

    public bool HasValidNamespace =>
        !string.IsNullOrWhiteSpace(EffectiveNamespace)
        && !EffectiveNamespace.StartsWith("set-automatically-", StringComparison.OrdinalIgnoreCase)
        && EffectiveNamespace.Length <= Constants.MaxNamespaceLength
        && new RegularExpressionAttribute(Constants.ValidNamespaceRegex).IsValid(EffectiveNamespace);
}
