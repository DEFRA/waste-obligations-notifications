using Defra.WasteObligations.Consumer.Startup;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed class NotSubmittedReminderWorker(
    ApplicationStartup startup,
    IOptions<NotSubmittedReminderOptions> options,
    INotSubmittedReminderScanService scanService,
    NotSubmittedReminderScanTrigger trigger,
    ILogger<NotSubmittedReminderWorker> logger
) : StartupBackgroundService(startup)
{
    protected override async Task ExecuteAfterStartup(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await scanService.RunFullScan(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (logger.IsEnabled(LogLevel.Error))
                {
                    logger.LogError(
                        exception,
                        "Not submitted reminder worker scan failed ({ReasonCode})",
                        "scan-failed"
                    );
                }
            }

            await trigger.WaitForRequest(options.Value.ScanInterval, stoppingToken);
        }
    }
}
