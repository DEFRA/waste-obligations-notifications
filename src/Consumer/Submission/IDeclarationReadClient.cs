using System.Text.Json;

namespace Defra.WasteObligations.Consumer.Submission;

public interface IDeclarationReadClient
{
    Task<JsonElement> Read(Guid organisationId, string declarationId, CancellationToken cancellationToken);
    Task CheckHealth(CancellationToken cancellationToken);
}
