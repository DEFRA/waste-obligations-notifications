using Defra.WasteObligations.Consumer.Data.Entities;
using Defra.WasteObligations.Consumer.Scheduled;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class ScheduledCampaignRunStoreTests : IntegrationTestBase
{
    private const string Campaign = "integration-campaign";
    private const int ObligationYear = 2026;
    private static readonly DateTimeOffset s_start = new(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan s_lease = TimeSpan.FromMinutes(10);
    private static readonly NotSubmittedReminderScanResult s_clean = new(3, 4, 1, 0, null);
    private static readonly NotSubmittedReminderScanResult s_partial = new(3, 2, 1, 2, null);

    [Fact]
    public async Task WhenAnotherHostHoldsLease_ShouldReportBusyUntilLeaseExpires()
    {
        await using var database = new RunDatabase();
        var clock = new MutableTimeProvider(s_start);
        var first = database.Store(clock);
        var second = database.Store(clock);

        var started = await first.TryStart(Campaign, ObligationYear, s_lease, TestContext.Current.CancellationToken);
        var contended = await second.TryStart(Campaign, ObligationYear, s_lease, TestContext.Current.CancellationToken);
        clock.Advance(s_lease);
        var takenOver = await second.TryStart(Campaign, ObligationYear, s_lease, TestContext.Current.CancellationToken);
        var staleRecord = await first.RecordScan(
            Campaign,
            ObligationYear,
            s_clean,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(ScheduledCampaignRunStart.Started, started);
        Assert.Equal(ScheduledCampaignRunStart.Busy, contended);
        Assert.Equal(ScheduledCampaignRunStart.Started, takenOver);
        Assert.False(staleRecord);
        var run = await second.Read(Campaign, ObligationYear, TestContext.Current.CancellationToken);
        Assert.NotNull(run);
        Assert.Equal(ScheduledCampaignRunStatus.InProgress, run.Status);
        Assert.Equal(2, run.ScanCount);
    }

    [Fact]
    public async Task WhenScanHasRetryableFailures_ShouldRecordCountsAndLeaveRunOpen()
    {
        await using var database = new RunDatabase();
        var store = database.Store(new MutableTimeProvider(s_start));

        await store.TryStart(Campaign, ObligationYear, s_lease, TestContext.Current.CancellationToken);
        var recorded = await store.RecordScan(
            Campaign,
            ObligationYear,
            s_partial,
            TestContext.Current.CancellationToken
        );
        var restarted = await store.TryStart(Campaign, ObligationYear, s_lease, TestContext.Current.CancellationToken);

        Assert.True(recorded);
        Assert.Equal(ScheduledCampaignRunStart.Started, restarted);
        var run = await store.Read(Campaign, ObligationYear, TestContext.Current.CancellationToken);
        Assert.NotNull(run);
        Assert.Equal(ScheduledCampaignRunStatus.InProgress, run.Status);
        Assert.Null(run.CompletedAtUtc);
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

        await store.TryStart(Campaign, ObligationYear, s_lease, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(3));
        var recorded = await store.RecordScan(Campaign, ObligationYear, s_clean, TestContext.Current.CancellationToken);
        clock.Advance(s_lease);
        var afterCompletion = await otherHost.TryStart(
            Campaign,
            ObligationYear,
            s_lease,
            TestContext.Current.CancellationToken
        );
        var completed = await store.Read(Campaign, ObligationYear, TestContext.Current.CancellationToken);
        await otherHost.Reopen(Campaign, ObligationYear, TestContext.Current.CancellationToken);
        var afterReopen = await otherHost.TryStart(
            Campaign,
            ObligationYear,
            s_lease,
            TestContext.Current.CancellationToken
        );

        Assert.True(recorded);
        Assert.Equal(ScheduledCampaignRunStart.Completed, afterCompletion);
        Assert.NotNull(completed);
        Assert.Equal(ScheduledCampaignRunStatus.Completed, completed.Status);
        Assert.Equal(new DateTime(2026, 12, 1, 10, 3, 0, DateTimeKind.Utc), completed.CompletedAtUtc);
        Assert.Null(completed.LeaseOwner);
        Assert.Equal(1, completed.ScanCount);
        Assert.Equal(ScheduledCampaignRunStart.Started, afterReopen);
        var reopened = await store.Read(Campaign, ObligationYear, TestContext.Current.CancellationToken);
        Assert.NotNull(reopened);
        Assert.Equal(ScheduledCampaignRunStatus.InProgress, reopened.Status);
        Assert.Null(reopened.CompletedAtUtc);
    }

    [Fact]
    public async Task WhenOwnerReleasesLease_ShouldAllowAnotherHostToStartImmediately()
    {
        await using var database = new RunDatabase();
        var clock = new MutableTimeProvider(s_start);
        var first = database.Store(clock);
        var second = database.Store(clock);

        await first.TryStart(Campaign, ObligationYear, s_lease, TestContext.Current.CancellationToken);
        await second.Release(Campaign, ObligationYear, TestContext.Current.CancellationToken);
        var stillHeld = await second.TryStart(Campaign, ObligationYear, s_lease, TestContext.Current.CancellationToken);
        await first.Release(Campaign, ObligationYear, TestContext.Current.CancellationToken);
        var released = await second.TryStart(Campaign, ObligationYear, s_lease, TestContext.Current.CancellationToken);

        Assert.Equal(ScheduledCampaignRunStart.Busy, stillHeld);
        Assert.Equal(ScheduledCampaignRunStart.Started, released);
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
