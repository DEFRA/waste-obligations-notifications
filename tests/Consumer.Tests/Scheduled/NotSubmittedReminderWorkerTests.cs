using Defra.WasteObligations.Consumer.Scheduled;
using Defra.WasteObligations.Consumer.Startup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Defra.WasteObligations.Consumer.Tests.Scheduled;

public sealed class NotSubmittedReminderWorkerTests
{
    [Fact]
    public async Task WhenDisabled_ShouldNotRunScan()
    {
        var startup = new ApplicationStartup();
        var scanService = Substitute.For<INotSubmittedReminderScanService>();
        var worker = CreateWorker(startup, scanService, enabled: false);

        var run = worker.StartAsync(TestContext.Current.CancellationToken);
        startup.MarkStarted();
        await worker.StopAsync(TestContext.Current.CancellationToken);
        await run;

        await scanService.DidNotReceive().RunFullScan(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenScanThrows_ShouldLogAndContinueUntilStopped()
    {
        var startup = new ApplicationStartup();
        var scanService = Substitute.For<INotSubmittedReminderScanService>();
        scanService
            .RunFullScan(Arg.Any<CancellationToken>())
            .Returns(
                callInfo =>
                    Task.FromException<NotSubmittedReminderScanResult>(new InvalidOperationException("scan failed")),
                callInfo => Task.FromResult(new NotSubmittedReminderScanResult(0, 0, 0, null))
            );

        var trigger = new NotSubmittedReminderScanTrigger(scanService);
        var worker = CreateWorker(startup, scanService, trigger, enabled: true);
        using var stopping = new CancellationTokenSource();

        var run = worker.StartAsync(stopping.Token);
        startup.MarkStarted();
        trigger.Request();
        await Task.Delay(TimeSpan.FromMilliseconds(200), stopping.Token);
        await stopping.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
        await run;

        await scanService.Received().RunFullScan(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenStoppingDuringScan_ShouldExitWithoutLoggingFailure()
    {
        var startup = new ApplicationStartup();
        var scanService = Substitute.For<INotSubmittedReminderScanService>();
        using var stopping = new CancellationTokenSource();
        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scanService
            .RunFullScan(Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                scanStarted.SetResult();
                await stopping.CancelAsync();
                throw new OperationCanceledException();
            });

        var worker = CreateWorker(startup, scanService, enabled: true);

        var run = worker.StartAsync(stopping.Token);
        startup.MarkStarted();
        await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await run;

        await scanService.Received(1).RunFullScan(Arg.Any<CancellationToken>());
    }

    private static NotSubmittedReminderWorker CreateWorker(
        ApplicationStartup startup,
        INotSubmittedReminderScanService scanService,
        NotSubmittedReminderScanTrigger? trigger = null,
        bool enabled = true
    )
    {
        var options = Options.Create(
            new NotSubmittedReminderOptions { Enabled = enabled, ScanInterval = TimeSpan.FromMilliseconds(50) }
        );
        trigger ??= new NotSubmittedReminderScanTrigger(scanService);
        using var logs = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace));

        return new NotSubmittedReminderWorker(
            startup,
            options,
            scanService,
            trigger,
            logs.CreateLogger<NotSubmittedReminderWorker>()
        );
    }
}
