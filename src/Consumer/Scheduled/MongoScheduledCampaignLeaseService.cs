using Defra.WasteObligations.Consumer.Data.Entities;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed class MongoScheduledCampaignLeaseService(Func<IMongoDatabase> database, TimeProvider timeProvider)
    : IScheduledCampaignLeaseService
{
    private const int DuplicateKeyErrorCode = 11000;

    private static readonly FilterDefinitionBuilder<ScheduledCampaignLease> s_filter =
        Builders<ScheduledCampaignLease>.Filter;

    private readonly string _instanceId = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    private readonly Lazy<IMongoCollection<ScheduledCampaignLease>> _collection = new(() =>
        database().GetCollection<ScheduledCampaignLease>(ScheduledCampaignLease.CollectionName)
    );

    private IMongoCollection<ScheduledCampaignLease> Leases => _collection.Value;

    public async Task<bool> TryAcquire(string leaseId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var filter = s_filter.And(
            s_filter.Eq(lease => lease.Id, leaseId),
            s_filter.Or(s_filter.Lte(lease => lease.ExpiresAt, utcNow), s_filter.Eq(lease => lease.Owner, _instanceId))
        );
        var update = Builders<ScheduledCampaignLease>
            .Update.Set(lease => lease.Owner, _instanceId)
            .Set(lease => lease.ExpiresAt, utcNow.Add(leaseDuration))
            .SetOnInsert(lease => lease.Id, leaseId);
        var options = new FindOneAndUpdateOptions<ScheduledCampaignLease>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After,
        };

        try
        {
            var result = await Leases.FindOneAndUpdateAsync(filter, update, options, cancellationToken);

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

    public async Task<bool> TryRenew(string leaseId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var filter = s_filter.And(
            s_filter.Eq(lease => lease.Id, leaseId),
            s_filter.Eq(lease => lease.Owner, _instanceId),
            s_filter.Gt(lease => lease.ExpiresAt, utcNow)
        );
        var update = Builders<ScheduledCampaignLease>.Update.Set(lease => lease.ExpiresAt, utcNow.Add(leaseDuration));
        var result = await Leases.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);

        return result.MatchedCount == 1;
    }

    public async Task Release(string leaseId, CancellationToken cancellationToken) =>
        await Leases.DeleteOneAsync(
            s_filter.And(s_filter.Eq(lease => lease.Id, leaseId), s_filter.Eq(lease => lease.Owner, _instanceId)),
            cancellationToken
        );
}
