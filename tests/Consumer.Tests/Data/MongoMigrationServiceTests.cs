using Defra.WasteObligations.Consumer.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests.Data;

public sealed class MongoMigrationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenLeaseRenewalFails_ShouldCancelMigrationAndReleaseLease(bool throws)
    {
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        lease.TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        lease
            .TryRenew(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
                throws
                    ? Task.FromException<bool>(new InvalidOperationException("Renewal failed"))
                    : Task.FromResult(false)
            );
        var runner = Substitute.For<IMongoMigrationRunner>();
        runner
            .Run(Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>()));
        using var service = CreateService(
            lease,
            runner,
            new MongoMigrationOptions
            {
                LeaseRenewalIntervalSeconds = 1,
                AttemptTimeoutSeconds = 2,
                MaximumAttempts = 1,
            }
        );

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await lease.Received(1).TryRenew(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await lease.Received(1).Release(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenMigrationTimesOut_ShouldCancelAttemptBeforeReleasingLease()
    {
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        lease.TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        var cancelled = false;
        var runner = Substitute.For<IMongoMigrationRunner>();
        runner
            .Run(Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                }
                finally
                {
                    cancelled = true;
                }
            });
        lease
            .Release(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Assert.True(cancelled);

                return Task.CompletedTask;
            });
        using var service = CreateService(
            lease,
            runner,
            new MongoMigrationOptions { AttemptTimeoutSeconds = 1, MaximumAttempts = 1 }
        );

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await lease.Received(1).Release(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenLeaseReleaseFails_ShouldCompleteWithoutRetryingMigration(bool cancelled)
    {
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        lease.TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        lease
            .Release(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException(
                    cancelled ? new OperationCanceledException() : new InvalidOperationException("Release failed")
                )
            );
        var runner = Substitute.For<IMongoMigrationRunner>();
        runner.Run(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        using var service = CreateService(lease, runner, new MongoMigrationOptions());

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await runner.Received(1).Run(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, 300)]
    [InlineData(true, 0)]
    public async Task WhenLeaseIsUnavailable_ShouldRetryUntilAcquired(bool throws, int alertThreshold)
    {
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        lease
            .TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(
                _ =>
                    throws
                        ? Task.FromException<bool>(new InvalidOperationException("Acquisition failed"))
                        : Task.FromResult(false),
                _ => Task.FromResult(true)
            );
        var runner = Substitute.For<IMongoMigrationRunner>();
        runner.Run(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        using var service = CreateService(
            lease,
            runner,
            new MongoMigrationOptions { LeaseAcquisitionAlertThresholdSeconds = alertThreshold }
        );

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        await lease.Received(2).TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await runner.Received(1).Run(Arg.Any<CancellationToken>());
        await lease.Received(1).Release(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenStoppedWhileWaitingForLease_ShouldNotRunMigrationOrReleaseAnotherHostsLease()
    {
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lease
            .TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(false)
            .AndDoes(_ => attempted.TrySetResult());
        var runner = Substitute.For<IMongoMigrationRunner>();
        using var service = CreateService(lease, runner, new MongoMigrationOptions());

        await service.StartAsync(TestContext.Current.CancellationToken);
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        await runner.DidNotReceive().Run(Arg.Any<CancellationToken>());
        await lease.DidNotReceive().Release(Arg.Any<CancellationToken>());
    }

    private static MongoMigrationService CreateService(
        IMongoMigrationLeaseService lease,
        IMongoMigrationRunner runner,
        MongoMigrationOptions options
    ) => new(lease, runner, Options.Create(options), TimeProvider.System, NullLogger<MongoMigrationService>.Instance);

    [Fact]
    public async Task WhenMigrationFails_ShouldRetryUnderSameLeaseAndReleaseAfterSuccess()
    {
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        lease.TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        var runner = Substitute.For<IMongoMigrationRunner>();
        runner
            .Run(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Migration failed")), Task.CompletedTask);
        using var service = new MongoMigrationService(
            lease,
            runner,
            Options.Create(new MongoMigrationOptions { RetryDelaySeconds = 1 }),
            TimeProvider.System,
            NullLogger<MongoMigrationService>.Instance
        );

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await runner.Received(2).Run(Arg.Any<CancellationToken>());
        await lease.Received(1).TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await lease.Received(1).Release(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenAttemptsAreExhausted_ShouldStopRetryingAndReleaseLease()
    {
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        lease.TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        var runner = Substitute.For<IMongoMigrationRunner>();
        runner
            .Run(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Migration failed")));
        using var service = new MongoMigrationService(
            lease,
            runner,
            Options.Create(new MongoMigrationOptions { MaximumAttempts = 1 }),
            TimeProvider.System,
            NullLogger<MongoMigrationService>.Instance
        );

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await runner.Received(1).Run(Arg.Any<CancellationToken>());
        await lease.Received(1).Release(Arg.Any<CancellationToken>());
    }
}
