using Defra.WasteObligations.Consumer.Data.Entities;
using Defra.WasteObligations.Consumer.Scheduled;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

public sealed class ScheduledCampaignLeaseTests : IntegrationTestBase
{
    private const string LeaseId = "integration-campaign:2026";
    private static readonly DateTimeOffset s_start = new(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan s_lease = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task WhenLeaseIsHeld_IsHeldShouldReturnTrueUntilReleasedOrExpired()
    {
        await using var database = new LeaseDatabase();
        var clock = new MutableTimeProvider(s_start);
        var leases = database.Leases(clock);
        var heldId = $"integration-held:{Guid.NewGuid():N}";

        Assert.False(await leases.IsHeld(heldId, TestContext.Current.CancellationToken));
        await leases.TryAcquire(heldId, s_lease, TestContext.Current.CancellationToken);
        Assert.True(await leases.IsHeld(heldId, TestContext.Current.CancellationToken));
        await leases.Release(heldId, TestContext.Current.CancellationToken);
        Assert.False(await leases.IsHeld(heldId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenConcurrentAcquireRaces_ShouldAllowOnlyOneOwner()
    {
        await using var database = new LeaseDatabase();
        var clock = new MutableTimeProvider(s_start);
        var first = database.Leases(clock);
        var second = database.Leases(clock);
        var raceId = $"integration-race:{Guid.NewGuid():N}";

        var results = await Task.WhenAll(
            first.TryAcquire(raceId, s_lease, TestContext.Current.CancellationToken),
            second.TryAcquire(raceId, s_lease, TestContext.Current.CancellationToken)
        );

        Assert.Equal(1, results.Count(acquired => acquired));
        Assert.Equal(1, results.Count(acquired => !acquired));
    }

    [Fact]
    public async Task WhenAnotherHostHoldsLease_ShouldRejectItUntilLeaseExpires()
    {
        await using var database = new LeaseDatabase();
        var clock = new MutableTimeProvider(s_start);
        var first = database.Leases(clock);
        var second = database.Leases(clock);

        var acquired = await first.TryAcquire(LeaseId, s_lease, TestContext.Current.CancellationToken);
        var contended = await second.TryAcquire(LeaseId, s_lease, TestContext.Current.CancellationToken);
        clock.Advance(s_lease);
        var takenOver = await second.TryAcquire(LeaseId, s_lease, TestContext.Current.CancellationToken);
        var staleRenewal = await first.TryRenew(LeaseId, s_lease, TestContext.Current.CancellationToken);
        var ownerRenewal = await second.TryRenew(LeaseId, s_lease, TestContext.Current.CancellationToken);

        Assert.True(acquired);
        Assert.False(contended);
        Assert.True(takenOver);
        Assert.False(staleRenewal);
        Assert.True(ownerRenewal);
    }

    [Fact]
    public async Task WhenNonOwnerReleasesLease_ShouldKeepOwnerLeaseUntilOwnerReleases()
    {
        await using var database = new LeaseDatabase();
        var clock = new MutableTimeProvider(s_start);
        var first = database.Leases(clock);
        var second = database.Leases(clock);

        await first.TryAcquire(LeaseId, s_lease, TestContext.Current.CancellationToken);
        await second.Release(LeaseId, TestContext.Current.CancellationToken);
        var stillHeld = await second.TryAcquire(LeaseId, s_lease, TestContext.Current.CancellationToken);
        await first.Release(LeaseId, TestContext.Current.CancellationToken);
        var released = await second.TryAcquire(LeaseId, s_lease, TestContext.Current.CancellationToken);

        Assert.False(stillHeld);
        Assert.True(released);
    }

    [Fact]
    public async Task WhenAnotherHostTakesOverDuringHold_ShouldCancelHoldAndKeepNewOwnerLease()
    {
        await using var database = new LeaseDatabase();
        var keeper = new ScheduledCampaignLeaseKeeper(
            database.Leases(TimeProvider.System),
            TimeProvider.System,
            NullLogger<ScheduledCampaignLeaseKeeper>.Instance
        );
        var otherHost = database.Leases(TimeProvider.System);

        var hold = await keeper.TryHold(
            LeaseId,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(hold);
        await database.ExpireLease(LeaseId);
        var takenOver = await otherHost.TryAcquire(LeaseId, s_lease, TestContext.Current.CancellationToken);
        await WaitForCancellation(hold.Token);
        var lost = hold.IsLost;
        await hold.DisposeAsync();
        var stillHeldByOtherHost = await database
            .Leases(TimeProvider.System)
            .TryAcquire(LeaseId, s_lease, TestContext.Current.CancellationToken);

        Assert.True(takenOver);
        Assert.True(lost);
        Assert.False(stillHeldByOtherHost);
    }

    private static async Task WaitForCancellation(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }

        Assert.Fail("Lease hold was not cancelled after takeover.");
    }

    private sealed class LeaseDatabase : IAsyncDisposable
    {
        private readonly IMongoClient _client = CreateMongoClient();
        private readonly string _name = $"campaign_lease_{Guid.NewGuid():N}";

        public MongoScheduledCampaignLeaseService Leases(TimeProvider clock) =>
            new(() => _client.GetDatabase(_name), clock);

        public Task<UpdateResult> ExpireLease(string leaseId) =>
            _client
                .GetDatabase(_name)
                .GetCollection<ScheduledCampaignLease>(ScheduledCampaignLease.CollectionName)
                .UpdateOneAsync(
                    Builders<ScheduledCampaignLease>.Filter.Eq(lease => lease.Id, leaseId),
                    Builders<ScheduledCampaignLease>.Update.Set(
                        lease => lease.ExpiresAt,
                        DateTime.UtcNow.AddMinutes(-1)
                    )
                );

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
