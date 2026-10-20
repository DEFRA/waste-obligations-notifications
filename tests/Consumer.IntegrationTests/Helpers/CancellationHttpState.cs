using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
using MongoDB.Bson;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Helpers;

public sealed class CancellationHttpState
{
    public string AccessToken { get; } = Guid.NewGuid().ToString("N");
    public Guid OrganisationId { get; } = Guid.NewGuid();
    public Guid SchemeId { get; } = Guid.NewGuid();
    public Guid SubmitterId { get; } = Guid.NewGuid();
    public string DeclarationId { get; } = ObjectId.GenerateNewId().ToString();
    public bool Scheme { get; init; }
    public string Country { get; init; } = "GB-ENG";
    public string Reason { get; set; } = "Producer requested to cancel";
    public int Version { get; set; } = 2;
    public int ReadStatus { get; set; } = 200;
    public IReadOnlyList<OrganisationPerson> Persons { get; set; } = [];
    public int DeclarationReads;
    public int LiveReads;
    public int TokenReads;
    public int SearchReads;
    public int PersonReads;

    public Declaration Declaration() =>
        new(
            DeclarationId,
            Version,
            2026,
            new DeclarationOrganisation(
                OrganisationId.ToString(),
                "GB-ENG",
                Scheme ? "ComplianceScheme" : "DirectProducer",
                "NRW",
                "stored-regulator@example.test"
            ),
            [
                new DeclarationAudit(
                    "Submitted",
                    "2026-09-20T11:12:13Z",
                    new DeclarationUser("Original Submitter", " submitter@example.test ", SubmitterId.ToString())
                ),
                new DeclarationAudit("Cancelled", "2026-10-01T12:00:00.123999Z", null, Reason),
            ]
        );
}
