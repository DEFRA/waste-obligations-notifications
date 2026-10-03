namespace Defra.WasteObligations.Consumer.Startup;

public abstract class StartupBackgroundService(ApplicationStartup startup) : BackgroundService
{
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await startup.Wait(stoppingToken);
        await ExecuteAfterStartup(stoppingToken);
    }

    protected abstract Task ExecuteAfterStartup(CancellationToken stoppingToken);
}
