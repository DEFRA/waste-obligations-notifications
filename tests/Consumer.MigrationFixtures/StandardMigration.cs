using MigrationVersion = AdaskoTheBeAsT.MongoDbMigrations.Abstractions.Version;

namespace Defra.WasteObligations.Consumer.MigrationFixtures;

public sealed class StandardMigration : TimedFixtureMigration
{
    protected override string Stage => "standard-2";

    public override MigrationVersion Version => new(2, 0, 0);
}
