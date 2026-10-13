using System.Text.Json;

namespace Defra.WasteObligations.Consumer.Submission;

public interface IDeclarationClient
{
    Task<JsonDocument> Read(SubmissionEvent submission, CancellationToken cancellationToken);

    Task<bool> CheckHealth(CancellationToken cancellationToken);
}
