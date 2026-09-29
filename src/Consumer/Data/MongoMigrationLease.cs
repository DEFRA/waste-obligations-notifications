namespace Defra.WasteObligations.Consumer.Data;

public sealed record MongoMigrationLease
{
    public required string Id { get; init; }

    public required string Owner { get; init; }

    public required DateTime ExpiresAt { get; init; }
}
