using Defra.WasteObligations.Consumer.Data.Entities;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed class MongoScheduledCampaignRunStore(Func<IMongoDatabase> database, TimeProvider timeProvider)
    : IScheduledCampaignRunStore
{
    private const int DuplicateKeyErrorCode = 11000;

    private static readonly FilterDefinitionBuilder<ScheduledCampaignRun> s_filter =
        Builders<ScheduledCampaignRun>.Filter;
    private static readonly UpdateDefinitionBuilder<ScheduledCampaignRun> s_update =
        Builders<ScheduledCampaignRun>.Update;

    private readonly string _instanceId = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    private readonly Lazy<IMongoCollection<ScheduledCampaignRun>> _collection = new(() =>
        database().GetCollection<ScheduledCampaignRun>(nameof(ScheduledCampaignRun))
    );

    private IMongoCollection<ScheduledCampaignRun> Runs => _collection.Value;

    public async Task<ScheduledCampaignRunStart> TryStart(
        string campaign,
        int obligationYear,
        TimeSpan lease,
        CancellationToken cancellationToken
    )
    {
        var id = RunId(campaign, obligationYear);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var filter = s_filter.And(
            s_filter.Eq(run => run.Id, id),
            s_filter.Ne(run => run.Status, ScheduledCampaignRunStatus.Completed),
            s_filter.Or(
                s_filter.Eq(run => run.LeaseExpiresAtUtc, null),
                s_filter.Lte(run => run.LeaseExpiresAtUtc, utcNow)
            )
        );
        var update = s_update
            .Set(run => run.LeaseOwner, _instanceId)
            .Set(run => run.LeaseExpiresAtUtc, utcNow.Add(lease))
            .Set(run => run.LastScanStartedAtUtc, utcNow)
            .Inc(run => run.ScanCount, 1)
            .SetOnInsert(run => run.Campaign, campaign)
            .SetOnInsert(run => run.ObligationYear, obligationYear)
            .SetOnInsert(run => run.Status, ScheduledCampaignRunStatus.InProgress);

        try
        {
            await Runs.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken);

            return ScheduledCampaignRunStart.Started;
        }
        catch (MongoCommandException exception) when (exception.Code == DuplicateKeyErrorCode)
        {
            return await ReadContendedStart(id, cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Code == DuplicateKeyErrorCode)
        {
            return await ReadContendedStart(id, cancellationToken);
        }
    }

    public async Task<bool> RecordScan(
        string campaign,
        int obligationYear,
        NotSubmittedReminderScanResult result,
        CancellationToken cancellationToken
    )
    {
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var update = s_update
            .Set(run => run.OrganisationsProcessed, result.OrganisationsProcessed)
            .Set(run => run.CommandsPublished, result.CommandsPublished)
            .Set(run => run.OrganisationsSkipped, result.OrganisationsSkipped)
            .Set(run => run.RetryableFailures, result.RetryableFailures)
            .Set(run => run.LastScanFinishedAtUtc, utcNow)
            .Unset(run => run.LeaseOwner)
            .Unset(run => run.LeaseExpiresAtUtc);
        if (result.RetryableFailures == 0)
        {
            update = update
                .Set(run => run.Status, ScheduledCampaignRunStatus.Completed)
                .Set(run => run.CompletedAtUtc, utcNow);
        }

        var recorded = await Runs.UpdateOneAsync(
            OwnedRun(campaign, obligationYear),
            update,
            cancellationToken: cancellationToken
        );

        return recorded.MatchedCount == 1;
    }

    public async Task Release(string campaign, int obligationYear, CancellationToken cancellationToken) =>
        await Runs.UpdateOneAsync(
            OwnedRun(campaign, obligationYear),
            s_update.Unset(run => run.LeaseOwner).Unset(run => run.LeaseExpiresAtUtc),
            cancellationToken: cancellationToken
        );

    public async Task Reopen(string campaign, int obligationYear, CancellationToken cancellationToken) =>
        await Runs.UpdateOneAsync(
            s_filter.Eq(run => run.Id, RunId(campaign, obligationYear)),
            s_update.Set(run => run.Status, ScheduledCampaignRunStatus.InProgress).Unset(run => run.CompletedAtUtc),
            cancellationToken: cancellationToken
        );

    public async Task<ScheduledCampaignRun?> Read(
        string campaign,
        int obligationYear,
        CancellationToken cancellationToken
    ) =>
        await Runs.Find(s_filter.Eq(run => run.Id, RunId(campaign, obligationYear)))
            .FirstOrDefaultAsync(cancellationToken);

    private static string RunId(string campaign, int obligationYear) => $"{campaign}:{obligationYear}";

    private FilterDefinition<ScheduledCampaignRun> OwnedRun(string campaign, int obligationYear) =>
        s_filter.And(
            s_filter.Eq(run => run.Id, RunId(campaign, obligationYear)),
            s_filter.Eq(run => run.LeaseOwner, _instanceId)
        );

    private async Task<ScheduledCampaignRunStart> ReadContendedStart(string id, CancellationToken cancellationToken)
    {
        var existing = await Runs.Find(s_filter.Eq(run => run.Id, id)).FirstOrDefaultAsync(cancellationToken);

        return existing?.Status == ScheduledCampaignRunStatus.Completed
            ? ScheduledCampaignRunStart.Completed
            : ScheduledCampaignRunStart.Busy;
    }
}
