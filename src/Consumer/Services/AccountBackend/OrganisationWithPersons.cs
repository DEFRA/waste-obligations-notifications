namespace Defra.WasteObligations.Consumer.Services.AccountBackend;

public sealed record OrganisationWithPersons(IReadOnlyList<OrganisationPerson>? Persons);
