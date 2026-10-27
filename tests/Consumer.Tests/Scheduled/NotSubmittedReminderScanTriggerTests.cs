using Defra.WasteObligations.Consumer.Scheduled;
using NSubstitute;
using Xunit;

namespace Defra.WasteObligations.Consumer.Tests.Scheduled;

public sealed class NotSubmittedReminderScanTriggerTests
{
    [Fact]
    public async Task WhenScanIsAllowed_ShouldReleaseWaitingWorkerWithoutRunningScan()
    {
        var scanService = Substitute.For<INotSubmittedReminderScanService>();
        scanService.GetSkipReason().Returns((string?)null);
        var trigger = new NotSubmittedReminderScanTrigger(scanService);

        var skipReason = trigger.Request();
        var wait = trigger.WaitForRequest(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        Assert.Null(skipReason);
        Assert.Same(
            wait,
            await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
        );
        await scanService.DidNotReceive().RunFullScan(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenScanIsNotAllowed_ShouldReturnReasonAndKeepWorkerWaiting()
    {
        var scanService = Substitute.For<INotSubmittedReminderScanService>();
        scanService.GetSkipReason().Returns(NotSubmittedReminderCutoverGuard.OutsideSendWindowReason);
        var trigger = new NotSubmittedReminderScanTrigger(scanService);

        var skipReason = trigger.Request();
        var wait = trigger.WaitForRequest(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        Assert.Equal(NotSubmittedReminderCutoverGuard.OutsideSendWindowReason, skipReason);
        Assert.NotSame(
            wait,
            await Task.WhenAny(wait, Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken))
        );
    }

    [Fact]
    public async Task WhenRequestedRepeatedly_ShouldCoalesceIntoOnePendingScan()
    {
        var scanService = Substitute.For<INotSubmittedReminderScanService>();
        scanService.GetSkipReason().Returns((string?)null);
        var trigger = new NotSubmittedReminderScanTrigger(scanService);

        trigger.Request();
        trigger.Request();
        await trigger.WaitForRequest(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        var second = trigger.WaitForRequest(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        Assert.NotSame(
            second,
            await Task.WhenAny(
                second,
                Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken)
            )
        );
    }

    [Fact]
    public async Task WhenNoRequestArrives_ShouldReturnAfterTimeout()
    {
        var trigger = new NotSubmittedReminderScanTrigger(Substitute.For<INotSubmittedReminderScanService>());

        var wait = trigger.WaitForRequest(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        Assert.Same(
            wait,
            await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
        );
        await wait;
    }

    [Fact]
    public async Task WhenStopping_ShouldCancelWait()
    {
        var trigger = new NotSubmittedReminderScanTrigger(Substitute.For<INotSubmittedReminderScanService>());
        using var stopping = new CancellationTokenSource();

        var wait = trigger.WaitForRequest(TimeSpan.FromMinutes(5), stopping.Token);
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }
}
