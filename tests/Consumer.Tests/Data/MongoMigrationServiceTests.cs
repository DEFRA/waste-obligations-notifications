using Defra.WasteObligations.Consumer.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests.Data;

public sealed class MongoMigrationServiceTests
{
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
