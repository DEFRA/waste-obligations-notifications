using System.ComponentModel.DataAnnotations;

namespace Defra.WasteObligations.Consumer.Consumers;

public sealed record AnalyticsEventConsumerOptions
{
    public const string SectionName = "AnalyticsEventConsumer";

    [Required]
    public required string QueueUrl { get; init; }

    public bool ProcessingEnabled { get; init; }

    [Range(0, 20)]
    public int WaitTimeSeconds { get; init; } = 20;

    [Range(1, 300)]
    public int ReceiveTimeoutSeconds { get; init; } = 30;
    [Range(1, 300)]
    public int ReadTimeoutSeconds { get; init; } = 20;
    [Range(1, 300)]
    public int PublishTimeoutSeconds { get; init; } = 10;
    [Range(1, 300)]
    public int DeleteTimeoutSeconds { get; init; } = 5;
    [Range(1, 300)]
    public int SafetyHeadroomSeconds { get; init; } = 10;
    [Range(1, 43200)]
    public int VisibilityTimeoutSeconds { get; init; } = 90;

    public bool HasValidProcessingBudget => ReceiveTimeoutSeconds > WaitTimeSeconds
        && VisibilityTimeoutSeconds >= ReceiveTimeoutSeconds + ReadTimeoutSeconds + PublishTimeoutSeconds + DeleteTimeoutSeconds + SafetyHeadroomSeconds;

    [Range(1, 300)]
    public int PollIntervalSeconds { get; init; } = 15;
}
