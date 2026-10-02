using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Data;

public sealed class MongoMigrationService(
    IMongoMigrationLeaseService leaseService,
    IMongoMigrationRunner migrationRunner,
    IOptions<MongoMigrationOptions> options,
    TimeProvider timeProvider,
    ILogger<MongoMigrationService> logger
) : BackgroundService
{
    private static readonly TimeSpan ReadinessCheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseReleaseTimeout = TimeSpan.FromSeconds(10);
    private int _attemptCount;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting Mongo migrations.");

        try
        {
            var failedChecks = 0;
            var readinessStartedAt = timeProvider.GetUtcNow();
            var readinessAlertLogged = false;

            while (!stoppingToken.IsCancellationRequested)
            {
                var leaseDuration = TimeSpan.FromSeconds(options.Value.LeaseDurationSeconds);
                bool acquired;
                long leaseRequestStartedAt;

                try
                {
                    if (await migrationRunner.CheckCompletion(stoppingToken))
                        return;

                    leaseRequestStartedAt = timeProvider.GetTimestamp();
                    acquired =
                        _attemptCount < options.Value.MaximumAttempts
                        && await leaseService.TryAcquire(leaseDuration, stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (failedChecks == 0)
                    {
                        logger.LogError(
                            exception,
                            "Mongo migration readiness check or lease acquisition failed. Retrying."
                        );
                    }
                    failedChecks++;
                    readinessAlertLogged = await WaitForReadinessRetry(
                        readinessStartedAt,
                        readinessAlertLogged,
                        stoppingToken
                    );
                    continue;
                }

                if (!acquired)
                {
                    readinessAlertLogged = await WaitForReadinessRetry(
                        readinessStartedAt,
                        readinessAlertLogged,
                        stoppingToken
                    );
                    continue;
                }

                if (await RunMigrationsWithLease(leaseDuration, leaseRequestStartedAt, stoppingToken))
                    return;

                LogAttemptExhaustionIfRequired();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
    }

    private void LogAttemptExhaustionIfRequired()
    {
        if (_attemptCount >= options.Value.MaximumAttempts)
        {
            logger.LogError(
                "Mongo migrations did not complete after {AttemptCount} attempt(s). No further attempts will be made by this host; checking for completion by another host.",
                _attemptCount
            );
        }
    }

    private bool LogReadinessAlertIfRequired(DateTimeOffset readinessStartedAt)
    {
        var readinessWaitDuration = timeProvider.GetUtcNow() - readinessStartedAt;
        var alertThreshold = TimeSpan.FromSeconds(options.Value.LeaseAcquisitionAlertThresholdSeconds);

        if (readinessWaitDuration < alertThreshold)
            return false;

        logger.LogError(
            "Mongo migrations have not completed after {ReadinessWaitDuration}. Pending Mongo migrations are being retried; critical completion determines deployment readiness.",
            readinessWaitDuration
        );

        return true;
    }

    private async Task<bool> WaitForReadinessRetry(
        DateTimeOffset readinessStartedAt,
        bool readinessAlertLogged,
        CancellationToken stoppingToken
    )
    {
        if (!readinessAlertLogged)
            readinessAlertLogged = LogReadinessAlertIfRequired(readinessStartedAt);

        await Task.Delay(ReadinessCheckInterval, timeProvider, stoppingToken);

        return readinessAlertLogged;
    }

    private async Task<bool> RunMigrationsWithLease(
        TimeSpan leaseDuration,
        long leaseRequestStartedAt,
        CancellationToken stoppingToken
    )
    {
        var remainingLeaseTime = GetRemainingLeaseTime(leaseDuration, leaseRequestStartedAt);
        if (remainingLeaseTime <= TimeSpan.Zero)
        {
            logger.LogError(
                "Mongo migration lease acquisition was not confirmed in time. Retrying without starting the engine."
            );
            await ReleaseLease();

            return false;
        }

        using var leaseExpiryCancellationTokenSource = new CancellationTokenSource(remainingLeaseTime, timeProvider);
        using var expiryRegistration = leaseExpiryCancellationTokenSource.Token.Register(() =>
            logger.LogError(
                "Mongo migration lease renewal was not confirmed before its deadline. Cancelling the migration engine."
            )
        );
        using var migrationCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            leaseExpiryCancellationTokenSource.Token
        );
        using var renewalCancellationTokenSource = new CancellationTokenSource();
        var renewalTask = RenewLease(
            leaseDuration,
            leaseRequestStartedAt,
            migrationCancellationTokenSource,
            leaseExpiryCancellationTokenSource,
            renewalCancellationTokenSource.Token
        );

        try
        {
            var maximumAttempts = options.Value.MaximumAttempts;

            while (_attemptCount < maximumAttempts && !migrationCancellationTokenSource.IsCancellationRequested)
            {
                var attempt = ++_attemptCount;
                if (await RunMigrationAttempt(attempt, migrationCancellationTokenSource.Token))
                    return true;

                if (migrationCancellationTokenSource.IsCancellationRequested)
                    return false;

                if (attempt < maximumAttempts)
                {
                    logger.LogWarning(
                        "Mongo migration attempt {Attempt} did not complete. Retrying in {RetryDelay} while retaining the lease.",
                        attempt,
                        TimeSpan.FromSeconds(options.Value.RetryDelaySeconds)
                    );
                    try
                    {
                        await Task.Delay(
                            TimeSpan.FromSeconds(options.Value.RetryDelaySeconds),
                            migrationCancellationTokenSource.Token
                        );
                    }
                    catch (OperationCanceledException) when (migrationCancellationTokenSource.IsCancellationRequested)
                    {
                        return false;
                    }
                }
            }
        }
        finally
        {
            await migrationCancellationTokenSource.CancelAsync();
            await renewalCancellationTokenSource.CancelAsync();

            await renewalTask;

            await ReleaseLease();
        }

        return false;
    }

    private async Task<bool> RunMigrationAttempt(int attempt, CancellationToken migrationCancellationToken)
    {
        // The runner bounds each actual migration separately; a batch deadline would truncate ordered prerequisites.
        var result = await WaitForMigrationAttempt(
            migrationRunner.Run(migrationCancellationToken),
            migrationCancellationToken
        );
        if (result.Exception is not null)
            logger.LogError(
                "Mongo migration attempt {Attempt} failed ({ExceptionType}).",
                attempt,
                result.Exception.GetType().Name
            );

        return result.Completed;
    }

    private static async Task<(bool Completed, Exception? Exception)> WaitForMigrationAttempt(
        Task migrationTask,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await migrationTask;

            return (true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, null);
        }
        catch (Exception exception)
        {
            return (false, exception);
        }
    }

    private async Task RenewLease(
        TimeSpan leaseDuration,
        long confirmedRequestStartedAt,
        CancellationTokenSource migrationCancellationTokenSource,
        CancellationTokenSource leaseExpiryCancellationTokenSource,
        CancellationToken renewalCancellationToken
    )
    {
        using var renewalTimer = new PeriodicTimer(
            TimeSpan.FromSeconds(options.Value.LeaseRenewalIntervalSeconds),
            timeProvider
        );
        using var requestCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            renewalCancellationToken,
            leaseExpiryCancellationTokenSource.Token
        );

        try
        {
            while (await renewalTimer.WaitForNextTickAsync(requestCancellationTokenSource.Token))
            {
                if (GetRemainingLeaseTime(leaseDuration, confirmedRequestStartedAt) <= TimeSpan.Zero)
                {
                    await leaseExpiryCancellationTokenSource.CancelAsync();

                    return;
                }

                var requestStartedAt = timeProvider.GetTimestamp();
                var renewed = await leaseService.TryRenew(leaseDuration, requestCancellationTokenSource.Token);
                if (renewalCancellationToken.IsCancellationRequested)
                    return;

                if (
                    HasLeaseConfirmationExpired(
                        leaseDuration,
                        confirmedRequestStartedAt,
                        leaseExpiryCancellationTokenSource
                    )
                )
                {
                    await leaseExpiryCancellationTokenSource.CancelAsync();

                    return;
                }

                if (renewed)
                {
                    var remainingLeaseTime = GetRemainingLeaseTime(leaseDuration, requestStartedAt);
                    if (remainingLeaseTime <= TimeSpan.Zero)
                    {
                        await leaseExpiryCancellationTokenSource.CancelAsync();

                        return;
                    }

                    confirmedRequestStartedAt = requestStartedAt;
                    leaseExpiryCancellationTokenSource.CancelAfter(remainingLeaseTime);
                    continue;
                }

                logger.LogError("Mongo migration lease was not renewed. Cancelling the migration engine.");

                await migrationCancellationTokenSource.CancelAsync();

                return;
            }
        }
        catch (OperationCanceledException)
            when (renewalCancellationToken.IsCancellationRequested
                || leaseExpiryCancellationTokenSource.IsCancellationRequested
            )
        {
            // Expected when migration processing stops.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Mongo migration lease renewal failed. Cancelling the migration engine.");

            await migrationCancellationTokenSource.CancelAsync();
        }
    }

    private bool HasLeaseConfirmationExpired(
        TimeSpan leaseDuration,
        long confirmedRequestStartedAt,
        CancellationTokenSource leaseExpiryCancellationTokenSource
    ) =>
        leaseExpiryCancellationTokenSource.IsCancellationRequested
        || GetRemainingLeaseTime(leaseDuration, confirmedRequestStartedAt) <= TimeSpan.Zero;

    private TimeSpan GetRemainingLeaseTime(TimeSpan leaseDuration, long requestStartedAt) =>
        leaseDuration
        - TimeSpan.FromSeconds(options.Value.LeaseRenewalIntervalSeconds / 2.0)
        - timeProvider.GetElapsedTime(requestStartedAt);

    private async Task ReleaseLease()
    {
        using var releaseCancellationTokenSource = new CancellationTokenSource(LeaseReleaseTimeout);

        try
        {
            await leaseService.Release(releaseCancellationTokenSource.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Mongo migration lease could not be released. It will expire automatically.");
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(exception, "Mongo migration lease release timed out. It will expire automatically.");
        }
    }
}
