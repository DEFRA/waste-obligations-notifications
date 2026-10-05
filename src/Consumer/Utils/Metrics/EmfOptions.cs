using System.ComponentModel.DataAnnotations;
using Amazon.CloudWatch.EMF;
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
