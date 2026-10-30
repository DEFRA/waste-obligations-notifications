using Defra.WasteObligations.Consumer.Notifications;

namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed class NotSubmittedReminderCampaign
{
    public const string Email1Id = "1";
    public const string Email2Id = "2";

    public static NotSubmittedReminderCampaign Email1 { get; } =
        new()
        {
            Id = Email1Id,
            SectionName = "NotSubmittedReminder",
            NotificationDefinition = NotificationDefinitionsOptions.DeclarationNotSubmittedReminder,
            NotificationTypePrefix = "declaration-not-submitted-reminder",
            YearPersonalisation = obligationYear => new Dictionary<string, object>
            {
                ["year"] = checked(obligationYear + 1),
            },
        };

    public static NotSubmittedReminderCampaign Email2 { get; } =
        new()
        {
            Id = Email2Id,
            SectionName = "NotSubmittedReminder2",
            NotificationDefinition = NotificationDefinitionsOptions.DeclarationNotSubmittedReminder2,
            NotificationTypePrefix = "declaration-not-submitted-reminder-2",
            RequiresRecyclingObligationsMet = true,
            RequiredPersonalisation = ["reportPackagingDataAccountUrl"],
            YearPersonalisation = obligationYear => new Dictionary<string, object>
            {
                ["obligationYear"] = obligationYear,
                ["certYear"] = checked(obligationYear + 1),
            },
        };

    public static IReadOnlyList<NotSubmittedReminderCampaign> All { get; } = [Email1, Email2];

    private static readonly string[] RecipientPersonalisation = ["firstName", "lastName", "regulatorEmail"];

    public required string Id { get; init; }

    public required string SectionName { get; init; }

    public required string NotificationDefinition { get; init; }

    public required string NotificationTypePrefix { get; init; }

    public bool RequiresRecyclingObligationsMet { get; init; }

    public IReadOnlyList<string> RequiredPersonalisation { get; init; } = [];

    public required Func<int, IReadOnlyDictionary<string, object>> YearPersonalisation { get; init; }

    public string Name => $"not-submitted-reminder-{Id}";

    public bool HasValidPersonalisation(IReadOnlyDictionary<string, string> configured)
    {
        var computed = RecipientPersonalisation
            .Concat(YearPersonalisation(2026).Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return configured.All(entry => !string.IsNullOrWhiteSpace(entry.Value) && !computed.Contains(entry.Key))
            && RequiredPersonalisation.All(required =>
                configured.Any(entry => string.Equals(entry.Key, required, StringComparison.OrdinalIgnoreCase))
            );
    }
}
