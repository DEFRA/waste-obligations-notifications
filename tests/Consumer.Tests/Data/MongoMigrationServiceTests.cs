using Defra.WasteObligations.Consumer.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests.Data;

public sealed class MongoMigrationServiceTests
{
    [Fact]
    public async Task WhenMigrationsAreAlreadyComplete_ShouldNotAcquireLeaseOrRunMigrations()
    {
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        var runner = Substitute.For<IMongoMigrationRunner>();
        runner.CheckReadiness(Arg.Any<CancellationToken>()).Returns(true);
        using var service = CreateService(lease, runner, new MongoMigrationOptions());

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await lease.DidNotReceive().TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await runner.DidNotReceive().Run(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenAnotherHostCompletesMigrationsWhileWaiting_ShouldCompleteWithoutOwningLease()
    {
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        lease.TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(false);
        var runner = Substitute.For<IMongoMigrationRunner>();
        runner.CheckReadiness(Arg.Any<CancellationToken>()).Returns(false, true);
        using var service = CreateService(lease, runner, new MongoMigrationOptions());

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await lease.Received(1).TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await runner.DidNotReceive().Run(Arg.Any<CancellationToken>());
        await lease.DidNotReceive().Release(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenReadinessCheckFails_ShouldRetryWithoutStoppingHost()
    {
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        var runner = Substitute.For<IMongoMigrationRunner>();
        runner
            .CheckReadiness(Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException<bool>(new InvalidOperationException("Mongo unavailable")),
                _ => Task.FromResult(true)
            );
        using var service = CreateService(lease, runner, new MongoMigrationOptions());

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await runner.Received(2).CheckReadiness(Arg.Any<CancellationToken>());
        await lease.DidNotReceive().TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenTimedOutMigrationDoesNotStop_ShouldRetainAndRenewLeaseUntilItStops()
    {
        var logger = Substitute.For<ILogger<MongoMigrationService>>();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        lease.TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        lease
            .TryRenew(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (cancelled.Task.IsCompleted)
                    renewed.TrySetResult();

                return true;
            });
        var runner = Substitute.For<IMongoMigrationRunner>();
        runner.CheckReadiness(Arg.Any<CancellationToken>()).Returns(false, true);
        runner
            .Run(Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var token = call.Arg<CancellationToken>();
                using var registration = token.Register(() => cancelled.TrySetResult());
                await finish.Task;
                token.ThrowIfCancellationRequested();
            });
        using var service = CreateService(
            lease,
            runner,
            new MongoMigrationOptions
            {
                AttemptTimeoutSeconds = 1,
                LeaseRenewalIntervalSeconds = 1,
                MaximumAttempts = 1,
            },
            logger
        );

        try
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
            await renewed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            await lease.DidNotReceive().Release(Arg.Any<CancellationToken>());
            Assert.False(service.ExecuteTask!.IsCompleted);
            AssertErrorLogged(logger, "Cancellation was requested");
        }
        finally
        {
            finish.TrySetResult();
            await service.StopAsync(TestContext.Current.CancellationToken);
        }

        await lease.Received(1).Release(Arg.Any<CancellationToken>());
        await runner.Received(1).Run(Arg.Any<CancellationToken>());
    }

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
        runner.CheckReadiness(Arg.Any<CancellationToken>()).Returns(false, true);
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
    public async Task WhenShutdownCancellationDoesNotStopEngine_ShouldRenewLeaseUntilItStops()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        lease.TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        lease
            .TryRenew(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                if (cancelled.Task.IsCompleted)
                    renewed.TrySetResult();

                return true;
            });
        var runner = Substitute.For<IMongoMigrationRunner>();
        runner.CheckReadiness(Arg.Any<CancellationToken>()).Returns(false);
        runner
            .Run(Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var token = call.Arg<CancellationToken>();
                using var registration = token.Register(() => cancelled.TrySetResult());
                started.TrySetResult();
                await finish.Task;
                token.ThrowIfCancellationRequested();
            });
        using var service = CreateService(lease, runner, new MongoMigrationOptions { LeaseRenewalIntervalSeconds = 1 });
        await service.StartAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var stopping = service.StopAsync(TestContext.Current.CancellationToken);

        try
        {
            await renewed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.True(cancelled.Task.IsCompleted);
            Assert.False(stopping.IsCompleted);
            await lease.DidNotReceive().Release(Arg.Any<CancellationToken>());
            await lease.Received(1).TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await runner.Received(1).Run(Arg.Any<CancellationToken>());
        }
        finally
        {
            finish.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        await lease.Received(1).Release(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenMigrationTimesOut_ShouldCancelAttemptBeforeReleasingLease()
    {
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        lease.TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        var cancelled = false;
        var runner = Substitute.For<IMongoMigrationRunner>();
        runner.CheckReadiness(Arg.Any<CancellationToken>()).Returns(false, true);
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
    public async Task WhenLeaseRenewalFailsWithAttemptsRemaining_ShouldReacquireAndComplete(bool throws)
    {
        var logger = Substitute.For<ILogger<MongoMigrationService>>();
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
            .Returns(
                call => Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>()),
                _ => Task.CompletedTask
            );
        using var service = CreateService(
            lease,
            runner,
            new MongoMigrationOptions
            {
                LeaseRenewalIntervalSeconds = 1,
                AttemptTimeoutSeconds = 10,
                MaximumAttempts = 2,
            },
            logger
        );

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await runner.Received(2).Run(Arg.Any<CancellationToken>());
        await lease.Received(2).TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await lease.Received(2).Release(Arg.Any<CancellationToken>());
        AssertErrorLogged(logger, throws ? "lease renewal failed" : "lease was not renewed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenRenewalCancellationDoesNotStopEngine_ShouldNotReleaseOrReacquireUntilItStops(bool throws)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
            .Returns(
                async call =>
                {
                    var token = call.Arg<CancellationToken>();
                    using var registration = token.Register(() => cancelled.TrySetResult());
                    await finish.Task;
                    token.ThrowIfCancellationRequested();
                },
                _ => Task.CompletedTask
            );
        using var service = CreateService(
            lease,
            runner,
            new MongoMigrationOptions
            {
                LeaseRenewalIntervalSeconds = 1,
                AttemptTimeoutSeconds = 10,
                MaximumAttempts = 2,
            }
        );

        try
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            await lease.DidNotReceive().Release(Arg.Any<CancellationToken>());
            await lease.Received(1).TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await runner.Received(1).Run(Arg.Any<CancellationToken>());
            Assert.False(service.ExecuteTask!.IsCompleted);

            finish.TrySetResult();
            await service.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            await lease.Received(2).Release(Arg.Any<CancellationToken>());
            await lease.Received(2).TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await runner.Received(2).Run(Arg.Any<CancellationToken>());
        }
        finally
        {
            finish.TrySetResult();
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenRenewalsExhaustAttemptsAcrossAcquisitions_ShouldOnlyObservePeerCompletion(bool throws)
    {
        var logger = Substitute.For<ILogger<MongoMigrationService>>();
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
        runner.CheckReadiness(Arg.Any<CancellationToken>()).Returns(false, false, false, true);
        runner
            .Run(Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>()));
        using var service = CreateService(
            lease,
            runner,
            new MongoMigrationOptions
            {
                LeaseRenewalIntervalSeconds = 1,
                AttemptTimeoutSeconds = 10,
                MaximumAttempts = 2,
            },
            logger
        );

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        await runner.Received(2).Run(Arg.Any<CancellationToken>());
        await lease.Received(2).TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await lease.Received(2).Release(Arg.Any<CancellationToken>());
        await runner.Received(4).CheckReadiness(Arg.Any<CancellationToken>());
        AssertErrorLogged(logger, "did not complete after 2 attempt(s)");
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
        var logger = Substitute.For<ILogger<MongoMigrationService>>();
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
            new MongoMigrationOptions { LeaseAcquisitionAlertThresholdSeconds = alertThreshold },
            logger
        );

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        await lease.Received(2).TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await runner.Received(1).Run(Arg.Any<CancellationToken>());
        await lease.Received(1).Release(Arg.Any<CancellationToken>());

        if (throws)
            AssertErrorLogged(logger, "readiness check or lease acquisition failed");

        if (alertThreshold == 0)
            AssertErrorLogged(logger, "Command consumption remains paused");
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
        MongoMigrationOptions options,
        ILogger<MongoMigrationService>? logger = null
    ) =>
        new(
            lease,
            runner,
            Options.Create(options),
            TimeProvider.System,
            logger ?? NullLogger<MongoMigrationService>.Instance
        );

    private static void AssertErrorLogged(ILogger<MongoMigrationService> logger, string expected)
    {
        Assert.Contains(
            logger.ReceivedCalls(),
            call =>
                call.GetArguments() is [LogLevel.Error, _, var state, _, _]
                && state?.ToString()?.Contains(expected, StringComparison.Ordinal) == true
        );
    }

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
    public async Task WhenAttemptsAreExhausted_ShouldReleaseLeaseAndObserveCompletionByAnotherHost()
    {
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        lease.TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        var runner = Substitute.For<IMongoMigrationRunner>();
        runner.CheckReadiness(Arg.Any<CancellationToken>()).Returns(false, true);
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
        await lease.Received(1).TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await runner.Received(2).CheckReadiness(Arg.Any<CancellationToken>());
    }
}
