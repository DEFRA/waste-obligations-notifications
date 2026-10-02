using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using MigrationVersion = AdaskoTheBeAsT.MongoDbMigrations.Abstractions.Version;

namespace Defra.WasteObligations.Consumer.Data.Migrations;

public abstract class MongoMigration : IMigration
{
    public virtual bool Critical => false;

    public abstract MigrationVersion Version { get; }

    public abstract string Name { get; }

    public abstract Task UpAsync(MigrationContext context);

    public abstract Task DownAsync(MigrationContext context);

    public abstract Task<bool> ValidateSchema(IMongoDatabase database, CancellationToken cancellationToken);

    protected static async Task CreateIndex<T>(
        MigrationContext context,
        string name,
        IndexKeysDefinition<T> keys,
        bool unique = false
    ) => await CreateIndex(context, typeof(T).Name, name, keys, unique);

    protected static async Task CreateIndex<T>(
        MigrationContext context,
        string collectionName,
        string name,
        IndexKeysDefinition<T> keys,
        bool unique = false
    )
    {
        var collection = context.Database.GetCollection<T>(collectionName);
        var requestedKeys = keys.Render(
            new RenderArgs<T>(collection.DocumentSerializer, collection.Settings.SerializerRegistry)
        );

        using (var cursor = await collection.Indexes.ListAsync(context.CancellationToken))
        {
            var existingIndexes = await cursor.ToListAsync(context.CancellationToken);
            var existingByName = existingIndexes.FirstOrDefault(x =>
                x.TryGetValue("name", out var indexName) && indexName == name
            );

            if (existingByName is not null)
            {
                var existingKeys = existingByName.GetValue("key", new BsonDocument()).AsBsonDocument;
                var existingUnique =
                    existingByName.TryGetValue("unique", out var existingUniqueValue)
                    && existingUniqueValue.IsBoolean
                    && existingUniqueValue.AsBoolean;

                if (existingKeys.Equals(requestedKeys) && existingUnique == unique)
                {
                    return;
                }

                await DropIndex(context, name, collection);
            }
        }

        var indexModel = new CreateIndexModel<T>(
            keys,
            new CreateIndexOptions
            {
                Name = name,
                Background = true,
                Unique = unique,
            }
        );

        await collection.Indexes.CreateOneAsync(indexModel, cancellationToken: context.CancellationToken);
    }

    protected static async Task DropIndex<T>(MigrationContext context, string name)
    {
        var collection = context.Database.GetCollection<T>(typeof(T).Name);

        await DropIndex(context, name, collection);
    }

    protected static async Task DropIndex<T>(MigrationContext context, string collectionName, string name)
    {
        var collection = context.Database.GetCollection<T>(collectionName);

        await DropIndex(context, name, collection);
    }

    private static async Task DropIndex<T>(MigrationContext context, string name, IMongoCollection<T> collection)
    {
        try
        {
            await collection.Indexes.DropOneAsync(name, context.CancellationToken);
        }
        catch (MongoCommandException exception)
        {
            if (exception.CodeName != "IndexNotFound")
            {
                throw;
            }
        }
    }
}
