using MigrationVersion = AdaskoTheBeAsT.MongoDbMigrations.Abstractions.Version;

namespace Defra.WasteObligations.Consumer.MigrationFixtures;

public sealed class LastCriticalMigration : TimedFixtureMigration
{
    protected override string Stage => "critical-3";

    public override MigrationVersion Version => new(3, 0, 0);

    public override bool Critical => true;
}
