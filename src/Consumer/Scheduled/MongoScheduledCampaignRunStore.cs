using Defra.WasteObligations.Consumer.Data.Entities;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.Scheduled;

// Callers must hold the campaign's scheduled lease; this store only records run state.
public sealed class MongoScheduledCampaignRunStore(Func<IMongoDatabase> database, TimeProvider timeProvider)
    : IScheduledCampaignRunStore
{
    private const int DuplicateKeyErrorCode = 11000;

    private static readonly FilterDefinitionBuilder<ScheduledCampaignRun> s_filter =
        Builders<ScheduledCampaignRun>.Filter;
    private static readonly UpdateDefinitionBuilder<ScheduledCampaignRun> s_update =
        Builders<ScheduledCampaignRun>.Update;

    private readonly Lazy<IMongoCollection<ScheduledCampaignRun>> _collection = new(() =>
        database().GetCollection<ScheduledCampaignRun>(nameof(ScheduledCampaignRun))
    );

    private IMongoCollection<ScheduledCampaignRun> Runs => _collection.Value;

    public async Task<ScheduledCampaignRunStart> TryStart(
        string campaign,
        int obligationYear,
        CancellationToken cancellationToken
    )
    {
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var filter = s_filter.And(
            s_filter.Eq(run => run.Id, RunId(campaign, obligationYear)),
            s_filter.Ne(run => run.Status, ScheduledCampaignRunStatus.Completed)
        );
        var update = s_update
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
            return ScheduledCampaignRunStart.Completed;
        }
        catch (MongoWriteException exception) when (exception.WriteError.Code == DuplicateKeyErrorCode)
        {
            return ScheduledCampaignRunStart.Completed;
        }
    }

    public async Task RecordScan(
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
            .Set(run => run.LastScanFinishedAtUtc, utcNow);
        if (result.RetryableFailures == 0)
        {
            update = update
                .Set(run => run.Status, ScheduledCampaignRunStatus.Completed)
                .Set(run => run.CompletedAtUtc, utcNow);
        }

        await Runs.UpdateOneAsync(
            s_filter.Eq(run => run.Id, RunId(campaign, obligationYear)),
            update,
            cancellationToken: cancellationToken
        );
    }

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
}
