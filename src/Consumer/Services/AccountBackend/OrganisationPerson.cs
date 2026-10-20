namespace Defra.WasteObligations.Consumer.Services.AccountBackend;

public sealed record OrganisationPerson(
    Guid? UserId,
    string? FirstName,
    string? LastName,
    string? Email,
    string? ServiceRole
);
