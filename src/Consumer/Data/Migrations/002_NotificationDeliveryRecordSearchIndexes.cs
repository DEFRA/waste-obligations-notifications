using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using Defra.WasteObligations.Consumer.Delivery;
using MongoDB.Bson;
using MongoDB.Driver;
using MigrationVersion = AdaskoTheBeAsT.MongoDbMigrations.Abstractions.Version;

namespace Defra.WasteObligations.Consumer.Data.Migrations;

[MigrationCollection(MongoNotificationDeliveryRecordStore.CollectionName, MigrationDirection.Both)]
public sealed class NotificationDeliveryRecordSearchIndexes : MongoMigration
{
    private const string RecordedAtField = "recordedAtUtc";
    private static readonly (string Name, BsonDocument Keys)[] s_indexes =
    [
        ("recordedAtUtc_id_desc", new BsonDocument { { RecordedAtField, -1 }, { "_id", -1 } }),
        (
            "notifyNotificationId_recordedAtUtc_id_desc",
            new BsonDocument { { "notifyNotificationId", 1 }, { RecordedAtField, -1 }, { "_id", -1 } }
        ),
        (
            "resendOfNotificationKey_recordedAtUtc_id_desc",
            new BsonDocument { { "resendOfNotificationKey", 1 }, { RecordedAtField, -1 }, { "_id", -1 } }
        ),
        (
            "outcome_recordedAtUtc_id_desc",
            new BsonDocument { { "outcome", 1 }, { RecordedAtField, -1 }, { "_id", -1 } }
        ),
    ];

    public override MigrationVersion Version => new(2, 0, 0);

    public override string Name => "002 - NotificationDeliveryRecord search indexes";

    public override async Task UpAsync(MigrationContext context)
    {
        var existing = await ListIndexes(context.Database, context.CancellationToken);
        foreach (var (name, keys) in s_indexes)
        {
            var index = existing.FirstOrDefault(index => index.GetValue("name", "") == name);
            if (index is not null && !Matches(index, keys))
                throw new InvalidOperationException("An existing index conflicts with the required search index.");
        }
        foreach (var (name, keys) in s_indexes)
        {
            await CreateIndex<BsonDocument>(
                context,
                MongoNotificationDeliveryRecordStore.CollectionName,
                name,
                new BsonDocumentIndexKeysDefinition<BsonDocument>(keys),
                replaceExisting: false
            );
        }
        if (!await ValidateSchema(context.Database, context.CancellationToken))
            throw new InvalidOperationException("Notification delivery-record search indexes are incomplete.");
    }

    public override async Task DownAsync(MigrationContext context)
    {
        foreach (var (name, _) in s_indexes)
            await DropIndex<BsonDocument>(context, MongoNotificationDeliveryRecordStore.CollectionName, name);
    }

    public override async Task<bool> ValidateSchema(IMongoDatabase database, CancellationToken cancellationToken)
    {
        var indexes = await ListIndexes(database, cancellationToken);
        foreach (var (name, keys) in s_indexes)
        {
            var index = indexes.FirstOrDefault(index => index.GetValue("name", "") == name);
            if (
                index is null
                || !Matches(index, keys)
                || !await HasCompletedIndex(
                    database,
                    MongoNotificationDeliveryRecordStore.CollectionName,
                    name,
                    cancellationToken
                )
            )
                return false;
        }

        return true;
    }

    private static bool Matches(BsonDocument index, BsonDocument keys) =>
        index.GetValue("key", new BsonDocument()).Equals(keys)
        && index.GetValue("unique", false) == BsonBoolean.False
        && index.GetValue("sparse", false) == BsonBoolean.False
        && index.GetValue("hidden", false) == BsonBoolean.False
        && !index.Contains("partialFilterExpression")
        && (
            !index.TryGetValue("collation", out var collation)
            || collation.Equals(new BsonDocument("locale", "simple"))
        );

    private static async Task<List<BsonDocument>> ListIndexes(IMongoDatabase database, CancellationToken token)
    {
        using var cursor = await database
            .GetCollection<BsonDocument>(MongoNotificationDeliveryRecordStore.CollectionName)
            .Indexes.ListAsync(token);

        return await cursor.ToListAsync(token);
    }
}
