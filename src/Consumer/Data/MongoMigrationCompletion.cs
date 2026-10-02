namespace Defra.WasteObligations.Consumer.Data;

public sealed class MongoMigrationCompletion
{
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsCompleted => _completed.Task.IsCompletedSuccessfully;

    public Task Wait(CancellationToken cancellationToken) => _completed.Task.WaitAsync(cancellationToken);

    public void MarkCompleted() => _completed.TrySetResult();
}
