using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using Defra.WasteObligations.Consumer.Delivery;
using MongoDB.Bson;
using MongoDB.Driver;
using MigrationVersion = AdaskoTheBeAsT.MongoDbMigrations.Abstractions.Version;

namespace Defra.WasteObligations.Consumer.Data.Migrations;

[MigrationCollection(MongoNotificationDeliveryRecordStore.CollectionName, MigrationDirection.Both)]
public sealed class NotificationDeliveryRecordParentSearchIndex : MongoMigration
{
    private const string ParentField = "resendOfNotificationKey";
    private const string IndexName = "resendOfNotificationKey_recordedAtUtc_id_desc";
    private static readonly BsonDocument s_keys = new()
    {
        { ParentField, 1 },
        { "recordedAtUtc", -1 },
        { "_id", -1 },
    };
    private static readonly BsonDocument s_partialFilter = new(ParentField, new BsonDocument("$type", "string"));

    public override MigrationVersion Version => new(3, 0, 0);

    public override string Name => "003 - NotificationDeliveryRecord parent search index";

    public override async Task UpAsync(MigrationContext context)
    {
        var existing = await ReadIndex(context.Database, context.CancellationToken);
        if (existing is not null && !Matches(existing))
            throw new InvalidOperationException("An existing index conflicts with the required parent search index.");

        var records = context.Database.GetCollection<BsonDocument>(MongoNotificationDeliveryRecordStore.CollectionName);
        await records.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                new BsonDocumentIndexKeysDefinition<BsonDocument>(s_keys),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = IndexName,
                    Background = true,
                    PartialFilterExpression = s_partialFilter,
                    Collation = Collation.Simple,
                }
            ),
            cancellationToken: context.CancellationToken
        );

        if (!await ValidateSchema(context.Database, context.CancellationToken))
            throw new InvalidOperationException("Notification delivery-record parent search index is incomplete.");
    }

    public override async Task DownAsync(MigrationContext context) =>
        await DropIndex<BsonDocument>(context, MongoNotificationDeliveryRecordStore.CollectionName, IndexName);

    public override async Task<bool> ValidateSchema(IMongoDatabase database, CancellationToken cancellationToken)
    {
        var index = await ReadIndex(database, cancellationToken);

        return index is not null
            && Matches(index)
            && await HasCompletedIndex(database, MongoNotificationDeliveryRecordStore.CollectionName, IndexName, cancellationToken);
    }

    private static bool Matches(BsonDocument index) =>
        index.GetValue("key", new BsonDocument()).Equals(s_keys)
        && index.GetValue("partialFilterExpression", new BsonDocument()).Equals(s_partialFilter)
        && index.GetValue("unique", false) == BsonBoolean.False
        && index.GetValue("sparse", false) == BsonBoolean.False
        && index.GetValue("hidden", false) == BsonBoolean.False
        && (!index.TryGetValue("collation", out var collation) || collation.Equals(new BsonDocument("locale", "simple")));

    private static async Task<BsonDocument?> ReadIndex(IMongoDatabase database, CancellationToken cancellationToken)
    {
        using var cursor = await database.GetCollection<BsonDocument>(MongoNotificationDeliveryRecordStore.CollectionName).Indexes.ListAsync(cancellationToken);
        var indexes = await cursor.ToListAsync(cancellationToken);

        return indexes.SingleOrDefault(index => index.GetValue("name", "") == IndexName);
    }
}
