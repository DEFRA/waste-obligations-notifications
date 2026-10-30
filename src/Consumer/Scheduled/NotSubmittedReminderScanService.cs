using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data.Entities;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteObligations;

namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed class NotSubmittedReminderScanService(
    NotSubmittedReminderScanSupport support,
    ScheduledCampaignLeaseKeeper leases,
    IScheduledCampaignRunStore runs,
    IWasteObligationsClient obligations,
    IAccountBackendClient accounts,
    INotificationCommandPublisher publisher,
    ILogger<NotSubmittedReminderScanService> logger
) : INotSubmittedReminderScanService
{
    public const string Campaign = "not-submitted-reminder-" + NotSubmittedReminderCommandFactory.CampaignId;
    public const string CompletedReason = "not-submitted-reminder-completed";
    public const string ScanInProgressReason = "not-submitted-reminder-scan-in-progress";
    public const string LeaseLostReason = "not-submitted-reminder-lease-lost";

    private const string DirectProducer = "DirectProducer";

    public async Task<string?> PrepareRequestedScan(bool reopen, CancellationToken cancellationToken)
    {
        var obligationYear = GetObligationYear();
        var skipReason = support.CutoverGuard.GetSkipReason(obligationYear);
        if (skipReason is not null)
            return skipReason;

        if (await leases.IsHeld(LeaseId(obligationYear), cancellationToken))
            return ScanInProgressReason;

        if (reopen)
        {
            await runs.Reopen(Campaign, obligationYear, cancellationToken);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Not submitted reminder run reopened for obligation year {ObligationYear}",
                    obligationYear
                );
            }

            return null;
        }

        var run = await runs.Read(Campaign, obligationYear, cancellationToken);

        return run?.Status == ScheduledCampaignRunStatus.Completed ? CompletedReason : null;
    }

    public async Task<NotSubmittedReminderScanResult> RunFullScan(CancellationToken cancellationToken)
    {
        var settings = support.Options.Value;
        var obligationYear = GetObligationYear();

        var skipReason = support.CutoverGuard.GetSkipReason(obligationYear);
        if (skipReason is not null)
        {
            if (
                skipReason != NotSubmittedReminderCutoverGuard.OutsideSendWindowReason
                && logger.IsEnabled(LogLevel.Warning)
            )
            {
                logger.LogWarning(
                    "Not submitted reminder scan skipped ({ReasonCode}) for obligation year {ObligationYear}",
                    skipReason,
                    obligationYear
                );
            }

            return Skipped(skipReason);
        }

        await using var lease = await leases.TryHold(
            LeaseId(obligationYear),
            settings.ScanLease,
            settings.ScanLeaseRenewalInterval,
            cancellationToken
        );
        if (lease is null)
            return Skipped(ScanInProgressReason);

        NotSubmittedReminderScanResult result;
        try
        {
            var start = await runs.TryStart(Campaign, obligationYear, lease.Token);
            if (start == ScheduledCampaignRunStart.Completed)
                return Skipped(CompletedReason);

            result = await Scan(obligationYear, lease.Token);

            await runs.RecordScan(Campaign, obligationYear, result, lease.Token);
        }
        catch (OperationCanceledException exception) when (lease.IsLost && !cancellationToken.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    exception,
                    "Not submitted reminder scan stopped ({ReasonCode}) for obligation year {ObligationYear}",
                    "lease-lost",
                    obligationYear
                );
            }

            return Skipped(LeaseLostReason);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Not submitted reminder scan completed for obligation year {ObligationYear}: {OrganisationsProcessed} organisations, {CommandsPublished} commands published, {OrganisationsSkipped} organisations skipped, {RetryableFailures} retryable failures, run {RunOutcome}",
                obligationYear,
                result.OrganisationsProcessed,
                result.CommandsPublished,
                result.OrganisationsSkipped,
                result.RetryableFailures,
                result.RetryableFailures == 0 ? "completed" : "incomplete"
            );
        }

        return result;
    }

    public async Task<NotSubmittedReminderRunStatus> GetRunStatus(CancellationToken cancellationToken)
    {
        var obligationYear = GetObligationYear();
        var occurrenceUtc = support.Occurrence.GetOccurrenceUtc(obligationYear);
        var run = await runs.Read(Campaign, obligationYear, cancellationToken);

        return new NotSubmittedReminderRunStatus(
            Campaign,
            obligationYear,
            occurrenceUtc,
            occurrenceUtc + support.Options.Value.SendWindow,
            run?.Status switch
            {
                null => "not-started",
                ScheduledCampaignRunStatus.Completed => "completed",
                _ => "in-progress",
            },
            run?.ScanCount ?? 0,
            run?.LastScanStartedAtUtc,
            run?.LastScanFinishedAtUtc,
            run?.CompletedAtUtc,
            run?.OrganisationsProcessed ?? 0,
            run?.CommandsPublished ?? 0,
            run?.OrganisationsSkipped ?? 0,
            run?.RetryableFailures ?? 0
        );
    }

    private static NotSubmittedReminderScanResult Skipped(string reason) => new(0, 0, 0, 0, reason);

    private static string LeaseId(int obligationYear) => $"{Campaign}:{obligationYear}";

    private int GetObligationYear() =>
        support.Options.Value.ObligationYearOverride ?? UkObligationYear.FromUtcNow(support.TimeProvider);

    private async Task<NotSubmittedReminderScanResult> Scan(int obligationYear, CancellationToken cancellationToken)
    {
        var actionOccurredAtUtc = support.Occurrence.GetOccurrenceUtc(obligationYear);
        var (organisations, listChanged) = await ReadUnsubmittedOrganisations(obligationYear, cancellationToken);
        var published = 0;
        var skipped = 0;
        var failures = listChanged ? 1 : 0;

        foreach (var row in organisations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await PublishForOrganisation(row, obligationYear, actionOccurredAtUtc, cancellationToken);
            if (outcome.Published == 0)
                skipped++;
            published += outcome.Published;
            failures += outcome.Failures;
        }

        return new NotSubmittedReminderScanResult(organisations.Count, published, skipped, failures, null);
    }

    // Offset paging can skip or repeat rows when the list changes between requests, so read every page first.
    private async Task<(
        IReadOnlyList<UnsubmittedOrganisationRow> Organisations,
        bool ListChanged
    )> ReadUnsubmittedOrganisations(int obligationYear, CancellationToken cancellationToken)
    {
        var pageSize = support.Options.Value.PageSize;
        var organisations = new List<UnsubmittedOrganisationRow>();
        var seen = new HashSet<Guid>();
        var page = 1;
        var read = 0;
        int? firstTotal = null;
        var total = int.MaxValue;

        while (read < total)
        {
            var pageResult = await obligations.SearchUnsubmittedOrganisations(
                obligationYear,
                DirectProducer,
                page,
                pageSize,
                cancellationToken
            );
            total = pageResult.Total;
            firstTotal ??= total;

            if (pageResult.UnsubmittedOrganisations.Count == 0)
                break;

            read += pageResult.UnsubmittedOrganisations.Count;
            organisations.AddRange(pageResult.UnsubmittedOrganisations.Where(row => seen.Add(row.OrganisationId)));
            page++;
        }

        var listChanged = firstTotal != total || organisations.Count < total;
        if (listChanged && logger.IsEnabled(LogLevel.Warning))
        {
            logger.LogWarning(
                "Not submitted reminder unsubmitted list changed during the scan ({ReasonCode}) for obligation year {ObligationYear}",
                "unsubmitted-list-changed",
                obligationYear
            );
        }

        return (organisations, listChanged);
    }

    private async Task<(int Published, int Failures)> PublishForOrganisation(
        UnsubmittedOrganisationRow row,
        int obligationYear,
        DateTimeOffset actionOccurredAtUtc,
        CancellationToken cancellationToken
    )
    {
        var (normalisedCountry, regulatorEmail, usedDefaultCountry) = RegulatorEmailResolver.Resolve(
            row.BusinessCountry
        );
        if (usedDefaultCountry && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Not submitted reminder defaulted business country ({ReasonCode}) for organisation {OrganisationId}",
                "default-business-country",
                row.OrganisationId
            );
        }

        OrganisationWithPersons? organisation;
        try
        {
            organisation = await accounts.ReadOrganisationWithPersons(row.OrganisationId, cancellationToken);
        }
        catch (Exception exception)
            when (exception is HttpRequestException or TaskCanceledException
                && !cancellationToken.IsCancellationRequested
            )
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    exception,
                    "Not submitted reminder skipped organisation {OrganisationId} ({ReasonCode})",
                    row.OrganisationId,
                    "account-read-unavailable"
                );
            }

            return (0, 1);
        }

        var recipients = NotSubmittedReminderRecipientResolver.Resolve(organisation?.Persons ?? []);
        if (recipients.Length == 0)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Not submitted reminder skipped organisation {OrganisationId} ({ReasonCode})",
                    row.OrganisationId,
                    "no-eligible-recipient"
                );
            }

            return (0, 0);
        }

        var published = 0;
        var failures = 0;
        foreach (var recipient in recipients)
        {
            var (notificationType, templateId, personalisation) = support.Presentation.Create(
                recipient,
                obligationYear,
                normalisedCountry,
                regulatorEmail
            );
            var command = NotSubmittedReminderCommandFactory.Create(
                obligationYear,
                row.OrganisationId,
                actionOccurredAtUtc,
                notificationType,
                templateId,
                recipient.Email,
                personalisation
            );

            if (await TryPublish(command, row.OrganisationId, cancellationToken))
                published++;
            else
                failures++;
        }

        return (published, failures);
    }

    private async Task<bool> TryPublish(
        NotificationCommand command,
        Guid organisationId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await publisher.Publish(command, cancellationToken);

            return true;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    exception,
                    "Not submitted reminder failed to publish for organisation {OrganisationId} ({ReasonCode})",
                    organisationId,
                    "command-publish-failed"
                );
            }

            return false;
        }
    }
}
