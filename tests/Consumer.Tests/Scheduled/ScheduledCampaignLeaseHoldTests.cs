using Defra.WasteObligations.Consumer.Scheduled;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Defra.WasteObligations.Consumer.Tests.Scheduled;

public sealed class ScheduledCampaignLeaseHoldTests
{
    private const string LeaseId = "not-submitted-reminder-1:2026";

    [Fact]
    public async Task TryHold_WhenAcquireFails_ShouldReturnNull()
    {
        var leases = Substitute.For<IScheduledCampaignLeaseService>();
        leases.TryAcquire(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(false);
        var keeper = CreateKeeper(leases);

        var hold = await keeper.TryHold(
            LeaseId,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken
        );

        Assert.Null(hold);
    }

    [Fact]
    public async Task TryHold_WhenLeaseHasNoConfirmationBudget_ShouldReturnNull()
    {
        var leases = Substitute.For<IScheduledCampaignLeaseService>();
        leases.TryAcquire(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        var keeper = CreateKeeper(leases);

        var hold = await keeper.TryHold(
            LeaseId,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken
        );

        Assert.Null(hold);
        await leases.Received(1).Release(LeaseId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryHold_WhenRenewalIsRejected_ShouldCancelScanToken()
    {
        var leases = Substitute.For<IScheduledCampaignLeaseService>();
        leases.TryAcquire(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        leases.TryRenew(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(false);
        var keeper = CreateKeeper(leases);

        var hold = await keeper.TryHold(
            LeaseId,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(hold);
        await WaitForAsync(() => hold.IsLost);
        await hold.DisposeAsync();
        await leases.Received(1).Release(LeaseId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryHold_WhenRenewalThrows_ShouldCancelScanToken()
    {
        var leases = Substitute.For<IScheduledCampaignLeaseService>();
        leases.TryAcquire(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        leases
            .TryRenew(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("mongo unavailable"));
        var keeper = CreateKeeper(leases);

        await using var hold = await keeper.TryHold(
            LeaseId,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(hold);
        await WaitForAsync(() => hold.IsLost);
    }

    [Fact]
    public async Task DisposeAsync_WhenReleaseFails_ShouldNotThrow()
    {
        var leases = Substitute.For<IScheduledCampaignLeaseService>();
        leases.TryAcquire(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        leases
            .Release(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("mongo unavailable"));
        var keeper = CreateKeeper(leases);
        var hold = await keeper.TryHold(
            LeaseId,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken
        );

        var exception = await Record.ExceptionAsync(async () => await hold!.DisposeAsync());

        Assert.Null(exception);
    }

    private static ScheduledCampaignLeaseKeeper CreateKeeper(IScheduledCampaignLeaseService leases)
    {
        using var logs = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace));

        return new ScheduledCampaignLeaseKeeper(
            leases,
            TimeProvider.System,
            logs.CreateLogger<ScheduledCampaignLeaseKeeper>()
        );
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.Fail("Condition was not met in time.");
    }
}
