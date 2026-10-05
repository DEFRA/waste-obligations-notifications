using MigrationVersion = AdaskoTheBeAsT.MongoDbMigrations.Abstractions.Version;

namespace Defra.WasteObligations.Consumer.MigrationFixtures;

public sealed class FirstCriticalMigration : TimedFixtureMigration
{
    protected override string Stage => "critical-1";

    public override MigrationVersion Version => new(1, 0, 0);

    public override bool Critical => true;
}
