using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteObligations;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Helpers;

public sealed class NotSubmittedReminderHttpState
{
    public string AccessToken { get; } = Guid.NewGuid().ToString("N");
    public int ObligationYear { get; init; }
    public IReadOnlyList<UnsubmittedOrganisationRow> Organisations { get; set; } = [];
    public Dictionary<Guid, IReadOnlyList<OrganisationPerson>> Persons { get; } = [];
    public int UnsubmittedReads;

    public Guid AddOrganisation(string country, params OrganisationPerson[] persons)
    {
        var organisationId = Guid.NewGuid();
        Organisations =
        [
            .. Organisations,
            new UnsubmittedOrganisationRow(
                organisationId,
                ObligationYear,
                "DirectProducer",
                country,
                $"Organisation {Organisations.Count + 1}",
                $"REF{Organisations.Count + 1}"
            ),
        ];
        Persons[organisationId] = persons;

        return organisationId;
    }

    public void RemoveOrganisation(Guid organisationId) =>
        Organisations = Organisations.Where(row => row.OrganisationId != organisationId).ToArray();
}
