using System.ComponentModel.DataAnnotations;

namespace Defra.WasteObligations.Consumer.Data;

public sealed record MongoMigrationOptions
{
    public const string SectionName = "MongoMigrations";

    [Range(10, 3600)]
    public int LeaseDurationSeconds { get; init; } = 30;

    [Range(1, 1800)]
    public int LeaseRenewalIntervalSeconds { get; init; } = 10;

    [Range(1, 86400)]
    public int CriticalOperationTimeoutSeconds { get; init; } = 20;

    [Range(1, 86400)]
    public int AttemptTimeoutSeconds { get; init; } = 300;

    [Range(1, 3600)]
    public int RetryDelaySeconds { get; init; } = 30;

    [Range(1, 86400)]
    public int LeaseAcquisitionAlertThresholdSeconds { get; init; } = 300;

    [Range(1, 10)]
    public int MaximumAttempts { get; init; } = 3;
}
