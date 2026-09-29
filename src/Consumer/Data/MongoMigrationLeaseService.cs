using Defra.WasteObligations.Consumer.Data.Entities;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.Data;

public sealed class MongoMigrationLeaseService(IMongoDatabase database, TimeProvider timeProvider)
    : IMongoMigrationLeaseService
{
    private const int DuplicateKeyErrorCode = 11000;
    private const string LeaseCollectionName = "_migrations_lease";
    private const string LeaseId = "mongo-migrations";

    private readonly string _instanceId = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    private readonly IMongoCollection<MongoMigrationLease> _lease = database.GetCollection<MongoMigrationLease>(
        LeaseCollectionName
    );

    public string InstanceId => _instanceId;

    public async Task<bool> TryAcquire(TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var filter = Builders<MongoMigrationLease>.Filter.And(
            Builders<MongoMigrationLease>.Filter.Eq(x => x.Id, LeaseId),
            Builders<MongoMigrationLease>.Filter.Or(
                Builders<MongoMigrationLease>.Filter.Lte(x => x.ExpiresAt, utcNow),
                Builders<MongoMigrationLease>.Filter.Eq(x => x.Owner, _instanceId)
            )
        );
        var update = Builders<MongoMigrationLease>
            .Update.Set(x => x.Owner, _instanceId)
            .Set(x => x.ExpiresAt, utcNow.Add(leaseDuration))
            .SetOnInsert(x => x.Id, LeaseId);
        var options = new FindOneAndUpdateOptions<MongoMigrationLease>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After,
        };

        try
        {
            var result = await _lease.FindOneAndUpdateAsync(filter, update, options, cancellationToken);

            return result is not null && result.Owner == _instanceId;
        }
        catch (MongoCommandException exception) when (exception.Code == DuplicateKeyErrorCode)
        {
            return false;
        }
        catch (MongoWriteException exception) when (exception.WriteError.Code == DuplicateKeyErrorCode)
        {
            return false;
        }
    }

    public async Task<bool> TryRenew(TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var filter = Builders<MongoMigrationLease>.Filter.And(
            Builders<MongoMigrationLease>.Filter.Eq(x => x.Id, LeaseId),
            Builders<MongoMigrationLease>.Filter.Eq(x => x.Owner, _instanceId)
        );
        var update = Builders<MongoMigrationLease>.Update.Set(x => x.ExpiresAt, utcNow.Add(leaseDuration));
        var result = await _lease.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);

        return result.MatchedCount == 1;
    }

    public async Task Release(CancellationToken cancellationToken)
    {
        var filter = Builders<MongoMigrationLease>.Filter.And(
            Builders<MongoMigrationLease>.Filter.Eq(x => x.Id, LeaseId),
            Builders<MongoMigrationLease>.Filter.Eq(x => x.Owner, _instanceId)
        );

        await _lease.DeleteOneAsync(filter, cancellationToken);
    }
}
