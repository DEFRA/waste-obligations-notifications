namespace Defra.WasteObligations.Consumer.Services.WasteObligations;

public interface IWasteObligationsClient
{
    Task<Declaration?> ReadDeclaration(Guid organisationId, string declarationId, CancellationToken cancellationToken);
    Task<UnsubmittedOrganisationsPage> SearchUnsubmittedOrganisations(
        int obligationYear,
        string registrationType,
        int page,
        int pageSize,
        CancellationToken cancellationToken
    );
    Task CheckHealth(CancellationToken cancellationToken);
}
