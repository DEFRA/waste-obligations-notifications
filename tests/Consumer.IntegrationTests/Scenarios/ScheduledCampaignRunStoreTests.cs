using Defra.WasteObligations.Consumer.Data.Entities;
using Defra.WasteObligations.Consumer.Scheduled;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class ScheduledCampaignRunStoreTests : IntegrationTestBase
{
    private const string Campaign = "integration-campaign";
    private const int ObligationYear = 2026;
    private static readonly DateTimeOffset s_start = new(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly NotSubmittedReminderScanResult s_clean = new(3, 4, 1, 0, null);
    private static readonly NotSubmittedReminderScanResult s_partial = new(3, 2, 1, 2, null);

    [Fact]
    public async Task WhenScanHasRetryableFailures_ShouldRecordCountsAndLeaveRunOpen()
    {
        await using var database = new RunDatabase();
        var store = database.Store(new MutableTimeProvider(s_start));

        await store.TryStart(Campaign, ObligationYear, TestContext.Current.CancellationToken);
        await store.RecordScan(Campaign, ObligationYear, s_partial, TestContext.Current.CancellationToken);
        var restarted = await store.TryStart(Campaign, ObligationYear, TestContext.Current.CancellationToken);

        Assert.Equal(ScheduledCampaignRunStart.Started, restarted);
        var run = await store.Read(Campaign, ObligationYear, TestContext.Current.CancellationToken);
        Assert.NotNull(run);
        Assert.Equal(ScheduledCampaignRunStatus.InProgress, run.Status);
        Assert.Null(run.CompletedAtUtc);
        Assert.Equal(2, run.ScanCount);
        Assert.Equal(
            (3, 2, 1, 2),
            (run.OrganisationsProcessed, run.CommandsPublished, run.OrganisationsSkipped, run.RetryableFailures)
        );
    }

    [Fact]
    public async Task WhenCleanScanRecorded_ShouldCompleteRunUntilReopened()
    {
        await using var database = new RunDatabase();
        var clock = new MutableTimeProvider(s_start);
        var store = database.Store(clock);
        var otherHost = database.Store(clock);

        await store.TryStart(Campaign, ObligationYear, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(3));
        await store.RecordScan(Campaign, ObligationYear, s_clean, TestContext.Current.CancellationToken);
        var afterCompletion = await otherHost.TryStart(Campaign, ObligationYear, TestContext.Current.CancellationToken);
        var completed = await store.Read(Campaign, ObligationYear, TestContext.Current.CancellationToken);
        await otherHost.Reopen(Campaign, ObligationYear, TestContext.Current.CancellationToken);
        var afterReopen = await otherHost.TryStart(Campaign, ObligationYear, TestContext.Current.CancellationToken);

        Assert.Equal(ScheduledCampaignRunStart.Completed, afterCompletion);
        Assert.NotNull(completed);
        Assert.Equal(ScheduledCampaignRunStatus.Completed, completed.Status);
        Assert.Equal(new DateTime(2026, 12, 1, 10, 3, 0, DateTimeKind.Utc), completed.CompletedAtUtc);
        Assert.Equal(1, completed.ScanCount);
        Assert.Equal(ScheduledCampaignRunStart.Started, afterReopen);
        var reopened = await store.Read(Campaign, ObligationYear, TestContext.Current.CancellationToken);
        Assert.NotNull(reopened);
        Assert.Equal(ScheduledCampaignRunStatus.InProgress, reopened.Status);
        Assert.Null(reopened.CompletedAtUtc);
        Assert.Equal(2, reopened.ScanCount);
    }

    private sealed class RunDatabase : IAsyncDisposable
    {
        private readonly IMongoClient _client = CreateMongoClient();
        private readonly string _name = $"campaign_run_{Guid.NewGuid():N}";

        public MongoScheduledCampaignRunStore Store(TimeProvider clock) => new(() => _client.GetDatabase(_name), clock);

        public async ValueTask DisposeAsync()
        {
            await _client.DropDatabaseAsync(_name, CancellationToken.None);
            _client.Dispose();
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
