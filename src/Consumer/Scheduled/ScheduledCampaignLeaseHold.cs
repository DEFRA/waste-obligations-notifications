namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed class ScheduledCampaignLeaseHold : IAsyncDisposable
{
    private static readonly TimeSpan s_releaseTimeout = TimeSpan.FromSeconds(10);

    private readonly IScheduledCampaignLeaseService _leases;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly string _leaseId;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeSpan _renewalInterval;
    private readonly CancellationTokenSource _expiry;
    private readonly CancellationTokenSource _scan;
    private readonly CancellationTokenSource _renewalStop = new();
    private readonly CancellationTokenRegistration _expiryRegistration;
    private readonly Task _renewal;

    internal ScheduledCampaignLeaseHold(
        IScheduledCampaignLeaseService leases,
        TimeProvider timeProvider,
        ILogger logger,
        ScheduledCampaignLeaseRequest request
    )
    {
        _leases = leases;
        _timeProvider = timeProvider;
        _logger = logger;
        _leaseId = request.LeaseId;
        _leaseDuration = request.LeaseDuration;
        _renewalInterval = request.RenewalInterval;

        var remaining = GetRemainingLeaseTime(request.AcquireRequestStartedAt);
        _expiry = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, timeProvider);
        _expiryRegistration = _expiry.Token.Register(LogLeaseLost);
        _scan = CancellationTokenSource.CreateLinkedTokenSource(request.StoppingToken, _expiry.Token);
        _renewal = _expiry.IsCancellationRequested ? Task.CompletedTask : Renew();
    }

    public CancellationToken Token => _scan.Token;

    public bool IsLost => _expiry.IsCancellationRequested;

    public async ValueTask DisposeAsync()
    {
        await _renewalStop.CancelAsync();
        await _renewal;
        await _expiryRegistration.DisposeAsync();

        using var releaseCancellation = new CancellationTokenSource(s_releaseTimeout);
        try
        {
            await _leases.Release(_leaseId, releaseCancellation.Token);
        }
        catch (Exception exception)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    exception,
                    "Scheduled campaign lease was not released ({ReasonCode})",
                    "lease-release-failed"
                );
            }
        }

        _scan.Dispose();
        _expiry.Dispose();
        _renewalStop.Dispose();
    }

    private async Task Renew()
    {
        using var timer = new PeriodicTimer(_renewalInterval, _timeProvider);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_renewalStop.Token, _expiry.Token);

        try
        {
            while (await timer.WaitForNextTickAsync(request.Token))
            {
                var requestStartedAt = _timeProvider.GetTimestamp();
                var renewed = await _leases.TryRenew(_leaseId, _leaseDuration, request.Token);
                var remaining = GetRemainingLeaseTime(requestStartedAt);
                if (!renewed || remaining <= TimeSpan.Zero)
                {
                    await _expiry.CancelAsync();

                    return;
                }

                _expiry.CancelAfter(remaining);
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            // Expected when the scan finishes or the lease deadline passes.
        }
        catch (Exception exception)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    exception,
                    "Scheduled campaign lease renewal failed ({ReasonCode})",
                    "lease-renewal-failed"
                );
            }

            await _expiry.CancelAsync();
        }
    }

    private void LogLeaseLost()
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "Scheduled campaign lease was not confirmed before its deadline ({ReasonCode})",
                "lease-lost"
            );
        }
    }

    // Half a renewal interval of headroom keeps this host's deadline ahead of another host's takeover.
    private TimeSpan GetRemainingLeaseTime(long requestStartedAt) =>
        _leaseDuration - _renewalInterval / 2 - _timeProvider.GetElapsedTime(requestStartedAt);
}
