namespace Defra.WasteObligations.Consumer.Services.WasteOrganisations;

public interface IWasteOrganisationsClient
{
    Task<Organisation?> Read(Guid organisationId, CancellationToken cancellationToken);
    Task CheckHealth(CancellationToken cancellationToken);
}
