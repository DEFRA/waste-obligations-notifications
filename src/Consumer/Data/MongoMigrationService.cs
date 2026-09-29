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
    private static readonly TimeSpan LeaseRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseReleaseTimeout = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting Mongo migrations.");

        try
        {
            var failedLeaseAcquisitions = 0;
            var leaseAcquisitionStartedAt = timeProvider.GetUtcNow();
            var leaseAcquisitionAlertLogged = false;

            while (!stoppingToken.IsCancellationRequested)
            {
                var leaseDuration = TimeSpan.FromSeconds(options.Value.LeaseDurationSeconds);
                bool acquired;

                try
                {
                    acquired = await leaseService.TryAcquire(leaseDuration, stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (failedLeaseAcquisitions == 0)
                    {
                        logger.LogError(exception, "Mongo migration lease acquisition failed. Retrying.");
                    }
                    failedLeaseAcquisitions++;
                    leaseAcquisitionAlertLogged = await WaitForLeaseRetry(
                        leaseAcquisitionStartedAt,
                        leaseAcquisitionAlertLogged,
                        stoppingToken
                    );
                    continue;
                }

                if (!acquired)
                {
                    leaseAcquisitionAlertLogged = await WaitForLeaseRetry(
                        leaseAcquisitionStartedAt,
                        leaseAcquisitionAlertLogged,
                        stoppingToken
                    );
                    continue;
                }

                await RunMigrationsWithLease(leaseDuration, stoppingToken);

                return;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
    }

    private bool LogLeaseAcquisitionAlertIfRequired(DateTimeOffset leaseAcquisitionStartedAt)
    {
        var leaseWaitDuration = timeProvider.GetUtcNow() - leaseAcquisitionStartedAt;
        var alertThreshold = TimeSpan.FromSeconds(options.Value.LeaseAcquisitionAlertThresholdSeconds);

        if (leaseWaitDuration < alertThreshold)
            return false;

        logger.LogError(
            "Mongo migration lease has not been acquired after {LeaseWaitDuration}. Retrying while the consumer remains healthy.",
            leaseWaitDuration
        );

        return true;
    }

    private async Task<bool> WaitForLeaseRetry(
        DateTimeOffset leaseAcquisitionStartedAt,
        bool leaseAcquisitionAlertLogged,
        CancellationToken stoppingToken
    )
    {
        if (!leaseAcquisitionAlertLogged)
            leaseAcquisitionAlertLogged = LogLeaseAcquisitionAlertIfRequired(leaseAcquisitionStartedAt);

        await Task.Delay(LeaseRetryDelay, timeProvider, stoppingToken);

        return leaseAcquisitionAlertLogged;
    }

    private async Task RunMigrationsWithLease(TimeSpan leaseDuration, CancellationToken stoppingToken)
    {
        using var migrationCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var renewalCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewalTask = RenewLease(
            leaseDuration,
            migrationCancellationTokenSource,
            renewalCancellationTokenSource.Token
        );

        try
        {
            var maximumAttempts = options.Value.MaximumAttempts;

            for (var attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                if (await RunMigrationAttempt(attempt, migrationCancellationTokenSource.Token, stoppingToken))
                    return;

                if (migrationCancellationTokenSource.IsCancellationRequested)
                    return;

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
                        return;
                    }
                }
            }

            logger.LogError(
                "Mongo migrations did not complete after {AttemptCount} attempt(s). No further attempts will be made by this host.",
                maximumAttempts
            );
        }
        finally
        {
            await migrationCancellationTokenSource.CancelAsync();
            await renewalCancellationTokenSource.CancelAsync();

            await renewalTask;

            await ReleaseLease();
        }
    }

    private async Task<bool> RunMigrationAttempt(
        int attempt,
        CancellationToken migrationCancellationToken,
        CancellationToken stoppingToken
    )
    {
        using var attemptCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            migrationCancellationToken
        );
        using var timeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var migrationTask = migrationRunner.Run(attemptCancellationTokenSource.Token);
        var timeout = TimeSpan.FromSeconds(options.Value.AttemptTimeoutSeconds);
        var timeoutTask = Task.Delay(timeout, timeoutCancellationTokenSource.Token);
        var completedTask = await Task.WhenAny(migrationTask, timeoutTask);
        await timeoutCancellationTokenSource.CancelAsync();

        if (completedTask != migrationTask)
        {
            if (stoppingToken.IsCancellationRequested || migrationCancellationToken.IsCancellationRequested)
                return (await WaitForMigrationAttempt(migrationTask, attemptCancellationTokenSource.Token)).Completed;

            logger.LogError(
                "Mongo migration attempt {Attempt} exceeded its {AttemptTimeout} limit. Cancellation was requested; the exclusive lease will be retained until the migration engine stops.",
                attempt,
                timeout
            );
            await attemptCancellationTokenSource.CancelAsync();

            return (await WaitForMigrationAttempt(migrationTask, attemptCancellationTokenSource.Token)).Completed;
        }

        var result = await WaitForMigrationAttempt(migrationTask, attemptCancellationTokenSource.Token);

        if (result.Exception is not null)
            logger.LogError(result.Exception, "Mongo migration attempt {Attempt} failed.", attempt);

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
        CancellationTokenSource migrationCancellationTokenSource,
        CancellationToken renewalCancellationToken
    )
    {
        using var renewalTimer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.LeaseRenewalIntervalSeconds));

        try
        {
            while (await renewalTimer.WaitForNextTickAsync(renewalCancellationToken))
            {
                if (await leaseService.TryRenew(leaseDuration, renewalCancellationToken))
                    continue;

                logger.LogError("Mongo migration lease was not renewed. Cancelling the migration engine.");

                await migrationCancellationTokenSource.CancelAsync();
            }
        }
        catch (OperationCanceledException) when (renewalCancellationToken.IsCancellationRequested)
        {
            // Expected when migration processing stops.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Mongo migration lease renewal failed. Cancelling the migration engine.");

            await migrationCancellationTokenSource.CancelAsync();
        }
    }

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
