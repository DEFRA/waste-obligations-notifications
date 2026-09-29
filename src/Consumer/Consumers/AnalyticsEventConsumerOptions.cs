using System.ComponentModel.DataAnnotations;

namespace Defra.WasteObligations.Consumer.Consumers;

public sealed record AnalyticsEventConsumerOptions
{
    public const string SectionName = "AnalyticsEventConsumer";

    [Required]
    public required string QueueUrl { get; init; }

    public bool ProcessingEnabled { get; init; }

    [Range(1, 10)]
    public int BatchSize { get; init; } = 10;

    [Range(0, 20)]
    public int WaitTimeSeconds { get; init; } = 20;

    [Range(1, 300)]
    public int PollIntervalSeconds { get; init; } = 15;
}
