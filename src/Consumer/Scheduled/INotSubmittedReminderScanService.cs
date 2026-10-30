namespace Defra.WasteObligations.Consumer.Scheduled;

public interface INotSubmittedReminderScanService
{
    Task<string?> PrepareRequestedScan(bool reopen, CancellationToken cancellationToken);

    Task<NotSubmittedReminderScanResult> RunFullScan(CancellationToken cancellationToken);

    Task<NotSubmittedReminderRunStatus> GetRunStatus(CancellationToken cancellationToken);
}
