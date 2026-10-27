namespace Defra.WasteObligations.Consumer.Scheduled;

public interface INotSubmittedReminderScanService
{
    string? GetSkipReason();

    Task<NotSubmittedReminderScanResult> RunFullScan(CancellationToken cancellationToken);
}
