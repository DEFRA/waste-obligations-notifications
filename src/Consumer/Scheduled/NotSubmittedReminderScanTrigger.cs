using System.Threading.Channels;

namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed class NotSubmittedReminderScanTrigger(INotSubmittedReminderScanService scanService)
{
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite }
    );

    public string? Request()
    {
        var skipReason = scanService.GetSkipReason();
        if (skipReason is null)
            _requests.Writer.TryWrite(true);

        return skipReason;
    }

    public async Task WaitForRequest(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        Task request = _requests.Reader.WaitToReadAsync(timeoutSource.Token).AsTask();
        await request.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        cancellationToken.ThrowIfCancellationRequested();
        _requests.Reader.TryRead(out _);
    }
}
