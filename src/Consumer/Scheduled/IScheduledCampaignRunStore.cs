using Defra.WasteObligations.Consumer.Data.Entities;

namespace Defra.WasteObligations.Consumer.Scheduled;

public interface IScheduledCampaignRunStore
{
    Task<ScheduledCampaignRunStart> TryStart(string campaign, int obligationYear, CancellationToken cancellationToken);

    Task RecordScan(
        string campaign,
        int obligationYear,
        NotSubmittedReminderScanResult result,
        CancellationToken cancellationToken
    );

    Task Reopen(string campaign, int obligationYear, CancellationToken cancellationToken);

    Task<ScheduledCampaignRun?> Read(string campaign, int obligationYear, CancellationToken cancellationToken);
}
