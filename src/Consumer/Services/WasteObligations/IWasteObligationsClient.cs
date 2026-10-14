namespace Defra.WasteObligations.Consumer.Services.WasteObligations;

public interface IWasteObligationsClient
{
    Task<Declaration?> ReadDeclaration(Guid organisationId, string declarationId, CancellationToken cancellationToken);
    Task CheckHealth(CancellationToken cancellationToken);
}
