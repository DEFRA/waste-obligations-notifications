using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteObligations;

namespace Defra.WasteObligations.Consumer.Scheduled;

public sealed class NotSubmittedReminderScanService(
    NotSubmittedReminderScanSupport support,
    IWasteObligationsClient obligations,
    IAccountBackendClient accounts,
    INotificationCommandPublisher publisher,
    ILogger<NotSubmittedReminderScanService> logger
) : INotSubmittedReminderScanService
{
    private const string DirectProducer = "DirectProducer";

    public string? GetSkipReason() => support.CutoverGuard.GetSkipReason(GetObligationYear());

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

            return new NotSubmittedReminderScanResult(0, 0, 0, skipReason);
        }

        var actionOccurredAtUtc = support.Occurrence.GetOccurrenceUtc(obligationYear);
        var page = 1;
        var processed = 0;
        var published = 0;
        var skipped = 0;
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
                var publishedForOrg = await PublishForOrganisation(
                    row,
                    obligationYear,
                    actionOccurredAtUtc,
                    cancellationToken
                );
                if (publishedForOrg == 0)
                    skipped++;
                published += publishedForOrg;
            }

            if (pageResult.UnsubmittedOrganisations.Count == 0)
                break;

            page++;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Not submitted reminder scan completed for obligation year {ObligationYear}: {OrganisationsProcessed} organisations, {CommandsPublished} commands published, {OrganisationsSkipped} organisations skipped",
                obligationYear,
                processed,
                published,
                skipped
            );
        }

        return new NotSubmittedReminderScanResult(processed, published, skipped, null);
    }

    private int GetObligationYear() =>
        support.Options.Value.ObligationYearOverride ?? UkObligationYear.FromUtcNow(support.TimeProvider);

    private async Task<int> PublishForOrganisation(
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

            return 0;
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

            return 0;
        }

        var count = 0;
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
                count++;
            }
            catch (Exception exception)
            {
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

        return count;
    }
}
