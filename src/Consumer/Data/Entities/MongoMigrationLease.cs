namespace Defra.WasteObligations.Consumer.Data.Entities;

public sealed record MongoMigrationLease
{
    public required string Id { get; init; }

    public required string Owner { get; init; }

    public required DateTime ExpiresAt { get; init; }
}
