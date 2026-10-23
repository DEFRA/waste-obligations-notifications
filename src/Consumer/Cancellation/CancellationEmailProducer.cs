using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Consumers;
using Defra.WasteObligations.Consumer.Notifications;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
using Defra.WasteObligations.Consumer.Services.WasteOrganisations;
using Defra.WasteObligations.Consumer.Submission;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Cancellation;

public sealed class CancellationEmailProducer(
    SubmittedEmailProducer submitted,
    IWasteOrganisationsClient organisations,
    IAccountBackendClient accounts,
    IOptions<NotificationDefinitionsOptions> options,
    ILogger<CancellationEmailProducer> logger
) : IAnalyticsNotificationProducer
{
    private const string InvalidDeclarationData = "invalid-declaration-data";

    public bool CanHandle(AnalyticsEvent analyticsEvent) => analyticsEvent.IsCancellation;

    public async Task<IReadOnlyList<NotificationCommand>> Create(
        Guid organisationId,
        string declarationId,
        int eventVersion,
        Declaration? declarationSnapshot,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var declaration = await submitted.Read(
                organisationId,
                declarationId,
                eventVersion,
                cancellationToken,
                declarationSnapshot
            );
            var (family, actionTime) = ReadCancellation(declaration);

            var live =
                await ReadDependency(
                    () => organisations.Read(organisationId, cancellationToken),
                    "organisation-read-unavailable"
                ) ?? throw new SubmissionProcessingException("organisation-not-found");
            if (live.Id != organisationId)
                throw new SubmissionProcessingException("identity-mismatch");

            var accountId =
                declaration.Organisation!.RegistrationType == "ComplianceScheme"
                    ? await ResolveScheme(live.CompaniesHouseNumber, cancellationToken)
                    : live.Id;
            if (accountId is null)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "Cancellation produced no notifications ({ReasonCode})",
                        "unresolved-compliance-scheme"
                    );
                }

                return [];
            }

            var organisation = await ReadDependency(
                () => accounts.ReadOrganisationWithPersons(accountId.Value, cancellationToken),
                "account-read-unavailable"
            );
            var recipients = ResolveRecipients(
                declaration.Audit?.FirstOrDefault(entry => entry.Action == "Submitted")?.User,
                organisation?.Persons ?? []
            );
            if (recipients.Length == 0)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "Cancellation produced no notifications ({ReasonCode})",
                        "no-eligible-recipient"
                    );
                }

                return [];
            }

            var predecessor = submitted.Create(declaration);
            var welsh = live.BusinessCountry == "GB-WLS";
            var template = Template(family, welsh);
            var commands = new List<NotificationCommand> { predecessor };
            foreach (var recipient in recipients)
            {
                var address = recipient.Email.ToLowerInvariant();
                var identity = Convert.ToHexStringLower(
                    SHA256.HashData(Encoding.UTF8.GetBytes($"{actionTime:O}\n{address}"))
                );
                var command = new NotificationCommand(
                    NotificationCommand.CurrentSchemaVersion,
                    $"cdec:{declarationId}:cancelled:v1:{identity}",
                    actionTime,
                    $"declaration-cancelled-{family}-{(welsh ? "cy" : "en")}",
                    address,
                    template,
                    CancellationPresentation.Create(declaration, welsh, recipient)
                );
                command.Validate();
                commands.Add(command);
            }

            return commands;
        }
        catch (SubmissionProcessingException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception
                    is JsonException
                        or InvalidOperationException
                        or FormatException
                        or InvalidDataException
                        or OverflowException
            )
        {
            throw new SubmissionProcessingException(InvalidDeclarationData);
        }
    }

    private static (string Family, DateTimeOffset ActionTime) ReadCancellation(Declaration declaration)
    {
        var audit =
            declaration.Audit?.FirstOrDefault(entry => entry.Action == "Cancelled")
            ?? throw new SubmissionProcessingException(InvalidDeclarationData);
        if (audit.Reason is null || !UtcTimestamp.TryParse(audit.Timestamp, out var actionTime))
            throw new SubmissionProcessingException(InvalidDeclarationData);

        if (declaration.ObligationYear <= 0 || string.IsNullOrWhiteSpace(declaration.Organisation!.RegistrationType))
            throw new SubmissionProcessingException(InvalidDeclarationData);

        var family = audit.Reason switch
        {
            "Not signed by correct person" => "not-signed-by-correct-person",
            "Recycling obligations changed" => "recycling-obligations-changed",
            "Producer can meet recycling obligations" or "Compliance scheme can meet recycling obligations" =>
                "can-meet-recycling-obligations",
            "Producer requested to cancel" or "Compliance scheme requested to cancel" => "producer-requested",
            _ => throw new SubmissionProcessingException("unknown-cancellation-reason"),
        };

        return (family, actionTime);
    }

    private async Task<Guid?> ResolveScheme(string? companiesHouseNumber, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(companiesHouseNumber))
            return null;

        var results = await ReadDependency(
            () => accounts.SearchOrganisationsByCompaniesHouseNumbers([companiesHouseNumber], cancellationToken),
            "account-search-unavailable"
        );
        var candidates = results
            .Where(candidate =>
                candidate.IsComplianceScheme
                && candidate.CompaniesHouseNumber == companiesHouseNumber
                && !string.IsNullOrWhiteSpace(candidate.ExternalId)
            )
            .ToArray();

        return candidates.Length == 1 && Guid.TryParse(candidates[0].ExternalId, out var id) ? id : null;
    }

    private static async Task<T> ReadDependency<T>(Func<Task<T>> read, string failureReason)
    {
        try
        {
            return await read();
        }
        catch (Exception exception)
            when (exception
                    is JsonException
                        or InvalidOperationException
                        or FormatException
                        or InvalidDataException
                        or OverflowException
            )
        {
            throw new SubmissionProcessingException(failureReason);
        }
    }

    private static CancellationRecipient[] ResolveRecipients(
        DeclarationUser? submitter,
        IReadOnlyList<OrganisationPerson> persons
    )
    {
        var result = new List<CancellationRecipient>();
        if (!string.IsNullOrWhiteSpace(submitter?.Email))
        {
            var email = submitter.Email.Trim();
            var person = persons.FirstOrDefault(person =>
                (Guid.TryParse(submitter.Id, out var userId) && person.UserId == userId)
                || string.Equals(person.Email, email, StringComparison.OrdinalIgnoreCase)
            );
            if (person is not null && ValidName(person))
                result.Add(new CancellationRecipient(email, person.FirstName!, person.LastName!));
        }

        var primary = persons.FirstOrDefault(person => person.ServiceRole == "Approved Person");
        if (primary is not null && ValidName(primary) && !string.IsNullOrWhiteSpace(primary.Email))
            result.Add(new CancellationRecipient(primary.Email.Trim(), primary.FirstName!, primary.LastName!));

        return result
            .DistinctBy(recipient => recipient.Email, StringComparer.OrdinalIgnoreCase)
            .OrderBy(recipient => recipient.Email, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ValidName(OrganisationPerson person) =>
        !string.IsNullOrWhiteSpace(person.FirstName) && !string.IsNullOrWhiteSpace(person.LastName);

    private string Template(string family, bool welsh)
    {
        var mappings = options.Value;

        return (family, welsh) switch
        {
            ("not-signed-by-correct-person", false) => mappings.GetTemplate(
                NotificationDefinitionsOptions.DeclarationCancelled,
                DeclarationNotificationTemplates.Cancelled.NotSignedByCorrectPersonEn
            ),
            ("not-signed-by-correct-person", true) => mappings.GetTemplate(
                NotificationDefinitionsOptions.DeclarationCancelled,
                DeclarationNotificationTemplates.Cancelled.NotSignedByCorrectPersonCy
            ),
            ("recycling-obligations-changed", false) => mappings.GetTemplate(
                NotificationDefinitionsOptions.DeclarationCancelled,
                DeclarationNotificationTemplates.Cancelled.RecyclingObligationsChangedEn
            ),
            ("recycling-obligations-changed", true) => mappings.GetTemplate(
                NotificationDefinitionsOptions.DeclarationCancelled,
                DeclarationNotificationTemplates.Cancelled.RecyclingObligationsChangedCy
            ),
            ("can-meet-recycling-obligations", false) => mappings.GetTemplate(
                NotificationDefinitionsOptions.DeclarationCancelled,
                DeclarationNotificationTemplates.Cancelled.CanMeetRecyclingObligationsEn
            ),
            ("can-meet-recycling-obligations", true) => mappings.GetTemplate(
                NotificationDefinitionsOptions.DeclarationCancelled,
                DeclarationNotificationTemplates.Cancelled.CanMeetRecyclingObligationsCy
            ),
            ("producer-requested", false) => mappings.GetTemplate(
                NotificationDefinitionsOptions.DeclarationCancelled,
                DeclarationNotificationTemplates.Cancelled.ProducerRequestedEn
            ),
            ("producer-requested", true) => mappings.GetTemplate(
                NotificationDefinitionsOptions.DeclarationCancelled,
                DeclarationNotificationTemplates.Cancelled.ProducerRequestedCy
            ),
            _ => throw new InvalidOperationException("Cancellation family invalid"),
        };
    }
}
