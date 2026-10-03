using System.Diagnostics;
using Defra.WasteObligations.Consumer.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests.Data;

public sealed class MongoMigrationHandoverTests
{
    [Fact]
    public async Task WhenCriticalAttemptFails_ShouldReleaseAndWaitFiveSecondsBeforeReacquiring()
    {
        var completion = new MongoMigrationCompletion();
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        var runner = Substitute.For<IMongoMigrationRunner>();
        var acquisitions = new List<long>();
        var releases = new List<long>();
        lease
            .TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                acquisitions.Add(Stopwatch.GetTimestamp());
                return true;
            });
        lease
            .Release(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                releases.Add(Stopwatch.GetTimestamp());
                return Task.CompletedTask;
            });
        var attempts = 0;
        runner
            .Run(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++attempts == 1)
                    return Task.FromException(new InvalidOperationException("failure"));
                completion.MarkCompleted();
                return Task.CompletedTask;
            });
        using var service = CreateService(lease, runner, completion);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(2, acquisitions.Count);
        Assert.Equal(2, releases.Count);
        Assert.True(Stopwatch.GetElapsedTime(releases[0], acquisitions[1]).TotalSeconds >= 4.9);
        Assert.True(completion.IsCompleted);
    }

    [Fact]
    public async Task WhenCriticalAttemptsAreExhausted_ShouldNotReacquireForAFourthAttempt()
    {
        var completion = new MongoMigrationCompletion();
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        var runner = Substitute.For<IMongoMigrationRunner>();
        var exhausted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerCompleted = false;
        var observedExhaustion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCount = 0;
        lease.TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        lease
            .Release(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref releaseCount) == 3)
                    exhausted.TrySetResult();
                return Task.CompletedTask;
            });
        runner
            .Run(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("failure")));
        runner
            .CheckCompletion(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var complete = Volatile.Read(ref peerCompleted);
                if (!complete)
                {
                    if (Volatile.Read(ref releaseCount) >= 3)
                        observedExhaustion.TrySetResult();

                    return false;
                }
                completion.MarkCompleted();
                return true;
            });
        using var service = CreateService(lease, runner, completion);
        try
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
            await exhausted.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            await observedExhaustion.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Volatile.Write(ref peerCompleted, true);
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await runner.Received(3).Run(Arg.Any<CancellationToken>());
            await lease.Received(3).TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            Assert.True(completion.IsCompleted);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task WhenStoppedDuringCriticalBackoff_ShouldNotAcquireAgain()
    {
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        var runner = Substitute.For<IMongoMigrationRunner>();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lease.TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        lease.Release(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask).AndDoes(_ => released.TrySetResult());
        runner
            .Run(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("failure")));
        using var service = CreateService(lease, runner, new MongoMigrationCompletion());

        await service.StartAsync(TestContext.Current.CancellationToken);
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        await lease.Received(1).TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await runner.Received(1).Run(Arg.Any<CancellationToken>());
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenCriticalReadinessIsEstablishedBeforeStandardFailure_ShouldRetryUnderSameLease()
    {
        var completion = new MongoMigrationCompletion();
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        var runner = Substitute.For<IMongoMigrationRunner>();
        lease.TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        var attempts = 0;
        runner
            .Run(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                completion.MarkCompleted();
                return ++attempts == 1
                    ? Task.FromException(new InvalidOperationException("standard failure"))
                    : Task.CompletedTask;
            });
        using var service = CreateService(
            lease,
            runner,
            completion,
            new MongoMigrationOptions { RetryDelaySeconds = 1 }
        );

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await lease.Received(1).TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await lease.Received(1).Release(Arg.Any<CancellationToken>());
        await runner.Received(2).Run(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenReadinessDependencyFails_ShouldKeepHostedServiceRunningWithoutPrivateExceptionLogs(
        bool cancelled
    )
    {
        const string privateData = "recipient@example.com secret-response";
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        var runner = Substitute.For<IMongoMigrationRunner>();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner
            .CheckCompletion(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                observed.TrySetResult();
                return Task.FromException<bool>(
                    cancelled ? new OperationCanceledException(privateData) : new InvalidOperationException(privateData)
                );
            });
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders().AddProvider(new TestLoggerProvider(logger)))
            .ConfigureServices(services =>
                services
                    .AddSingleton(lease)
                    .AddSingleton(runner)
                    .AddSingleton(new MongoMigrationCompletion())
                    .AddSingleton(TimeProvider.System)
                    .AddSingleton<IOptions<MongoMigrationOptions>>(Options.Create(new MongoMigrationOptions()))
                    .AddHostedService<MongoMigrationService>()
            )
            .Build();

        try
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            wait.CancelAfter(TimeSpan.FromSeconds(5));
            while (
                !logger
                    .ReceivedCalls()
                    .Any(call =>
                        call.GetArguments() is [LogLevel.Error, _, var state, _, _]
                        && state?.ToString()?.Contains("readiness-check-or-acquisition", StringComparison.Ordinal)
                            == true
                    )
            )
                await Task.Delay(TimeSpan.FromMilliseconds(10), wait.Token);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.Contains(
            logger.ReceivedCalls(),
            call =>
                call.GetArguments() is [LogLevel.Error, _, var state, null, _]
                && state?.ToString()?.Contains("readiness-check-or-acquisition", StringComparison.Ordinal) == true
        );
        Assert.All(
            logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log)),
            call =>
            {
                Assert.Null(call.GetArguments()[3]);
                Assert.DoesNotContain(privateData, call.GetArguments()[2]?.ToString() ?? "", StringComparison.Ordinal);
            }
        );
        await runner.DidNotReceive().Run(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenReadinessDependencyThrowsAfterShutdown_ShouldNotFaultHostedServiceOrExposePrivateData()
    {
        const string privateData = "recipient@example.com secret-response";
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = Substitute.For<IMongoMigrationLeaseService>();
        var runner = Substitute.For<IMongoMigrationRunner>();
        async Task<bool> FailAfterCancellation(NSubstitute.Core.CallInfo call)
        {
            using var registration = call.Arg<CancellationToken>().Register(() => cancelled.TrySetResult());
            pending.TrySetResult();
            await cancelled.Task;
            throw new InvalidOperationException(privateData);
        }
        runner.CheckCompletion(Arg.Any<CancellationToken>()).Returns(FailAfterCancellation);
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders().AddProvider(new TestLoggerProvider(logger)))
            .ConfigureServices(services =>
                services
                    .AddSingleton(lease)
                    .AddSingleton(runner)
                    .AddSingleton(new MongoMigrationCompletion())
                    .AddSingleton(TimeProvider.System)
                    .AddSingleton<IOptions<MongoMigrationOptions>>(Options.Create(new MongoMigrationOptions()))
                    .AddHostedService<MongoMigrationService>()
            )
            .Build();
        try
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            await pending.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        var service = Assert.Single(host.Services.GetServices<IHostedService>().OfType<MongoMigrationService>());
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.True(cancelled.Task.IsCompletedSuccessfully);
        Assert.All(
            logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log)),
            call =>
            {
                Assert.Null(call.GetArguments()[3]);
                Assert.DoesNotContain(privateData, call.GetArguments()[2]?.ToString() ?? "", StringComparison.Ordinal);
            }
        );
        await lease.DidNotReceive().TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await runner.DidNotReceive().Run(Arg.Any<CancellationToken>());
    }

    private static MongoMigrationService CreateService(
        IMongoMigrationLeaseService lease,
        IMongoMigrationRunner runner,
        MongoMigrationCompletion completion,
        MongoMigrationOptions? settings = null
    ) =>
        new(
            lease,
            runner,
            completion,
            Options.Create(settings ?? new MongoMigrationOptions()),
            TimeProvider.System,
            NullLogger<MongoMigrationService>.Instance
        );

    private sealed class TestLoggerProvider(ILogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose() { }
    }
}
