using Defra.WasteObligations.Consumer.Data;

namespace Defra.WasteObligations.Consumer.Tests.Data;

public sealed class MongoMigrationReadinessTests
{
    [Fact]
    public async Task WhenMigrationsHaveNotCompleted_ShouldWaitUntilCompletion()
    {
        var readiness = new MongoMigrationReadiness();
        var waiting = readiness.Wait(TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        readiness.MarkCompleted();
        await waiting;
        await readiness.Wait(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WhenWaitIsCancelled_ShouldAllowOtherWaitersToComplete()
    {
        var readiness = new MongoMigrationReadiness();
        using var cancellation = new CancellationTokenSource();
        var waiting = readiness.Wait(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        readiness.MarkCompleted();
        await readiness.Wait(TestContext.Current.CancellationToken);
    }
}
