using Defra.WasteObligations.Consumer.Services.AccountBackend;

namespace Defra.WasteObligations.Consumer.Scheduled;

public static class NotSubmittedReminderRecipientResolver
{
    private const string ApprovedPersonRole = "Approved Person";
    private const string DelegatedPersonRole = "Delegated Person";

    public static NotSubmittedReminderRecipient[] Resolve(IReadOnlyList<OrganisationPerson> persons)
    {
        var result = new List<NotSubmittedReminderRecipient>();

        var approved = persons.FirstOrDefault(person => person.ServiceRole == ApprovedPersonRole);
        if (approved is not null && ValidName(approved) && !string.IsNullOrWhiteSpace(approved.Email))
            result.Add(
                new NotSubmittedReminderRecipient(approved.Email.Trim(), approved.FirstName!, approved.LastName!)
            );

        var delegated = persons.FirstOrDefault(person => person.ServiceRole == DelegatedPersonRole);
        if (delegated is not null && ValidName(delegated) && !string.IsNullOrWhiteSpace(delegated.Email))
            result.Add(
                new NotSubmittedReminderRecipient(delegated.Email.Trim(), delegated.FirstName!, delegated.LastName!)
            );

        return result
            .DistinctBy(recipient => recipient.Email, StringComparer.OrdinalIgnoreCase)
            .OrderBy(recipient => recipient.Email, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ValidName(OrganisationPerson person) =>
        !string.IsNullOrWhiteSpace(person.FirstName) && !string.IsNullOrWhiteSpace(person.LastName);
}
