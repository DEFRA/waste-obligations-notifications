using System.ComponentModel.DataAnnotations;

namespace Defra.WasteObligations.Consumer.Administration;

public sealed record CommandDlqAdministrationOptions
{
    public const string SectionName = "CommandDlqAdministration";

    [Required]
    public string QueueUrl { get; init; } = "set-automatically-when-deployed";

    public int SelectionLifetimeSeconds { get; init; } = 120;

    public int DependencyTimeoutSeconds { get; init; } = 10;

    public bool HasValidSelectionBudget =>
        DependencyTimeoutSeconds > 0
        && DependencyTimeoutSeconds < SelectionLifetimeSeconds
        && SelectionLifetimeSeconds < 300;
}
