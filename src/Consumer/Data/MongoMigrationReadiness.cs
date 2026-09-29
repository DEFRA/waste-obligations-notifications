namespace Defra.WasteObligations.Consumer.Data;

public sealed class MongoMigrationReadiness
{
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Wait(CancellationToken cancellationToken) => _completed.Task.WaitAsync(cancellationToken);

    public void MarkCompleted() => _completed.TrySetResult();
}
