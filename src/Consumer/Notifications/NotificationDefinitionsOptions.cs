namespace Defra.WasteObligations.Consumer.Notifications;

public sealed class NotificationDefinition
{
    public Dictionary<string, string> Templates { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class NotificationDefinitionsOptions
{
    public const string SectionName = "Notifications";
    public const string DeclarationSubmitted = "DeclarationSubmitted";
    public const string DeclarationCancelled = "DeclarationCancelled";
    public const string DeclarationNotSubmittedReminder = "DeclarationNotSubmittedReminder";

    public Dictionary<string, NotificationDefinition> Definitions { get; init; } = CreateDefaults();

    public string GetTemplate(string definition, string template) => Definitions[definition].Templates[template];

    public bool HasValidTemplates =>
        Definitions.Values.All(definition =>
            definition.Templates.Count > 0
            && definition.Templates.Values.All(value => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty)
        )
        && CreateDefaults()
            .All(required =>
                Definitions.TryGetValue(required.Key, out var definition)
                && required.Value.Templates.Keys.All(definition.Templates.ContainsKey)
            );

    private static Dictionary<string, NotificationDefinition> CreateDefaults() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [DeclarationSubmitted] = new()
            {
                Templates = new(StringComparer.OrdinalIgnoreCase)
                {
                    [DeclarationNotificationTemplates.Submitted.DirectEnglishTemplateId] =
                        "5f64e3bd-d454-4a45-a9c6-9409bf940d7a",
                    [DeclarationNotificationTemplates.Submitted.DirectWelshTemplateId] =
                        "b3223b0b-a467-40c1-9150-f78b76d11fd8",
                    [DeclarationNotificationTemplates.Submitted.SchemeEnglishTemplateId] =
                        "b103685d-de8a-4ea9-abc8-d244ca26841b",
                    [DeclarationNotificationTemplates.Submitted.SchemeWelshTemplateId] =
                        "95d8c2aa-a229-4ecb-9774-78ec06d0cbac",
                },
            },
            [DeclarationCancelled] = new()
            {
                Templates = new(StringComparer.OrdinalIgnoreCase)
                {
                    [DeclarationNotificationTemplates.Cancelled.NotSignedByCorrectPersonEn] =
                        "1502cfa2-9758-4410-b189-d1f95a7c774f",
                    [DeclarationNotificationTemplates.Cancelled.NotSignedByCorrectPersonCy] =
                        "7cab1c5e-9edf-4139-b3fd-3db4bcf3041d",
                    [DeclarationNotificationTemplates.Cancelled.RecyclingObligationsChangedEn] =
                        "857808d4-d159-421e-92b6-bf9d59711a9e",
                    [DeclarationNotificationTemplates.Cancelled.RecyclingObligationsChangedCy] =
                        "0a966633-b7ad-42dd-b286-af0cb2405ed2",
                    [DeclarationNotificationTemplates.Cancelled.CanMeetRecyclingObligationsEn] =
                        "86345150-cb25-4b59-94c3-578bed7e82d4",
                    [DeclarationNotificationTemplates.Cancelled.CanMeetRecyclingObligationsCy] =
                        "308e7224-0ab1-4f54-8e8d-1558c9c57a77",
                    [DeclarationNotificationTemplates.Cancelled.ProducerRequestedEn] =
                        "3e03c93f-955c-4db3-936c-bfa3f7725a5f",
                    [DeclarationNotificationTemplates.Cancelled.ProducerRequestedCy] =
                        "e419a544-e1b3-4ea5-b8eb-a074e63aea1a",
                },
            },
            [DeclarationNotSubmittedReminder] = new()
            {
                Templates = new(StringComparer.OrdinalIgnoreCase)
                {
                    [DeclarationNotificationTemplates.NotSubmittedReminder.EnglishTemplateId] =
                        "54acaa9f-93c4-4052-bd0d-f5bc9e122ce6",
                    [DeclarationNotificationTemplates.NotSubmittedReminder.WelshTemplateId] =
                        "156214dd-3735-4ed0-bb8d-c955facc7dd0",
                },
            },
        };
}
