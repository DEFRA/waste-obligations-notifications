namespace Defra.WasteObligations.Consumer.Services.AccountBackend;

public interface IAccountBackendClient
{
    Task<IReadOnlyList<AccountOrganisation>> SearchOrganisationsByCompaniesHouseNumbers(
        IReadOnlyCollection<string> companiesHouseNumbers,
        CancellationToken cancellationToken
    );
    Task<OrganisationWithPersons?> ReadOrganisationWithPersons(
        Guid organisationId,
        CancellationToken cancellationToken
    );
    Task CheckHealth(CancellationToken cancellationToken);
}
