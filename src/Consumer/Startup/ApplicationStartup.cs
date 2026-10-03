namespace Defra.WasteObligations.Consumer.Startup;

public sealed class ApplicationStartup
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsStarted => _started.Task.IsCompletedSuccessfully;

    public Task Wait(CancellationToken cancellationToken) => _started.Task.WaitAsync(cancellationToken);

    public void MarkStarted() => _started.TrySetResult();
}
