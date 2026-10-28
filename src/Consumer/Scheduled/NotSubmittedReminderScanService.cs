using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Data.Entities;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteObligations;

namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed class NotSubmittedReminderScanService(
    NotSubmittedReminderScanSupport support,
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

    private const string DirectProducer = "DirectProducer";

    public async Task<string?> PrepareRequestedScan(bool reopen, CancellationToken cancellationToken)
    {
        var obligationYear = GetObligationYear();
        var skipReason = support.CutoverGuard.GetSkipReason(obligationYear);
        if (skipReason is not null)
            return skipReason;

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

        var start = await runs.TryStart(Campaign, obligationYear, settings.ScanLease, cancellationToken);
        if (start != ScheduledCampaignRunStart.Started)
            return Skipped(start == ScheduledCampaignRunStart.Completed ? CompletedReason : ScanInProgressReason);

        NotSubmittedReminderScanResult result;
        try
        {
            result = await Scan(obligationYear, cancellationToken);
        }
        catch
        {
            await ReleaseAfterFailure(obligationYear);
            throw;
        }

        var recorded = await runs.RecordScan(Campaign, obligationYear, result, cancellationToken);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Not submitted reminder scan completed for obligation year {ObligationYear}: {OrganisationsProcessed} organisations, {CommandsPublished} commands published, {OrganisationsSkipped} organisations skipped, {RetryableFailures} retryable failures, run {RunOutcome}",
                obligationYear,
                result.OrganisationsProcessed,
                result.CommandsPublished,
                result.OrganisationsSkipped,
                result.RetryableFailures,
                RunOutcome(recorded, result)
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

    private static string RunOutcome(bool recorded, NotSubmittedReminderScanResult result)
    {
        if (!recorded)
            return "lease-lost";

        return result.RetryableFailures == 0 ? "completed" : "incomplete";
    }

    private int GetObligationYear() =>
        support.Options.Value.ObligationYearOverride ?? UkObligationYear.FromUtcNow(support.TimeProvider);

    private async Task<NotSubmittedReminderScanResult> Scan(int obligationYear, CancellationToken cancellationToken)
    {
        var settings = support.Options.Value;
        var actionOccurredAtUtc = support.Occurrence.GetOccurrenceUtc(obligationYear);
        var page = 1;
        var processed = 0;
        var published = 0;
        var skipped = 0;
        var failures = 0;
        var total = int.MaxValue;

        while (processed < total)
        {
            var pageResult = await obligations.SearchUnsubmittedOrganisations(
                obligationYear,
                DirectProducer,
                page,
                settings.PageSize,
                cancellationToken
            );
            total = pageResult.Total;

            foreach (var row in pageResult.UnsubmittedOrganisations)
            {
                processed++;
                var outcome = await PublishForOrganisation(row, obligationYear, actionOccurredAtUtc, cancellationToken);
                if (outcome.Published == 0)
                    skipped++;
                published += outcome.Published;
                failures += outcome.Failures;
            }

            if (pageResult.UnsubmittedOrganisations.Count == 0)
                break;

            page++;
        }

        return new NotSubmittedReminderScanResult(processed, published, skipped, failures, null);
    }

    private async Task ReleaseAfterFailure(int obligationYear)
    {
        try
        {
            await runs.Release(Campaign, obligationYear, CancellationToken.None);
        }
        catch (Exception exception)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    exception,
                    "Not submitted reminder run lease was not released ({ReasonCode}) for obligation year {ObligationYear}",
                    "run-release-failed",
                    obligationYear
                );
            }
        }
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
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
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

            try
            {
                await publisher.Publish(command, cancellationToken);
                published++;
            }
            catch (Exception exception)
            {
                failures++;
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning(
                        exception,
                        "Not submitted reminder failed to publish for organisation {OrganisationId} ({ReasonCode})",
                        row.OrganisationId,
                        "command-publish-failed"
                    );
                }
            }
        }

        return (published, failures);
    }
}
