using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Defra.WasteObligations.Consumer.Commands;
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
    IOptions<CancellationOptions> options
)
{
    public async Task<IReadOnlyList<NotificationCommand>> Create(
        Guid organisationId,
        string declarationId,
        int eventVersion,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var declaration = await submitted.Read(organisationId, declarationId, eventVersion, cancellationToken);
            var audit =
                declaration.Audit?.FirstOrDefault(entry => entry.Action == "Cancelled")
                ?? throw new SubmissionProcessingException("invalid-declaration-data");
            if (audit.Reason is null || !UtcTimestamp.TryParse(audit.Timestamp, out var actionTime))
                throw new SubmissionProcessingException("invalid-declaration-data");

            if (
                declaration.ObligationYear <= 0
                || string.IsNullOrWhiteSpace(declaration.Organisation!.RegistrationType)
            )
                throw new SubmissionProcessingException("invalid-declaration-data");

            var family = audit.Reason switch
            {
                "Not signed by correct person" => "not-signed-by-correct-person",
                "Recycling obligations changed" => "recycling-obligations-changed",
                "Producer can meet recycling obligations" or "Compliance scheme can meet recycling obligations" =>
                    "can-meet-recycling-obligations",
                "Producer requested to cancel" or "Compliance scheme requested to cancel" => "producer-requested",
                _ => null,
            };
            if (family is null)
                return [];

            var live =
                await organisations.Read(organisationId, cancellationToken)
                ?? throw new SubmissionProcessingException("organisation-not-found");
            if (live.Id != organisationId)
                throw new SubmissionProcessingException("identity-mismatch");

            var accountId =
                declaration.Organisation.RegistrationType == "ComplianceScheme"
                    ? await ResolveScheme(live.CompaniesHouseNumber, cancellationToken)
                    : live.Id;
            if (accountId is null)
                return [];

            var organisation = await accounts.ReadOrganisationWithPersons(accountId.Value, cancellationToken);
            var recipients = ResolveRecipients(
                declaration.Audit?.FirstOrDefault(entry => entry.Action == "Submitted")?.User,
                organisation?.Persons ?? []
            );
            if (recipients.Count == 0)
                return [];

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
            throw new SubmissionProcessingException("invalid-declaration-data");
        }
    }

    private async Task<Guid?> ResolveScheme(string? companiesHouseNumber, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(companiesHouseNumber))
            return null;

        var results = await accounts.SearchOrganisationsByCompaniesHouseNumbers(
            [companiesHouseNumber],
            cancellationToken
        );
        var candidates = (results ?? throw new SubmissionProcessingException("account-search-unavailable"))
            .Where(candidate =>
                candidate.IsComplianceScheme
                && candidate.CompaniesHouseNumber == companiesHouseNumber
                && !string.IsNullOrWhiteSpace(candidate.ExternalId)
            )
            .ToArray();

        return candidates.Length == 1 && Guid.TryParse(candidates[0].ExternalId, out var id) ? id : null;
    }

    private static IReadOnlyList<CancellationRecipient> ResolveRecipients(
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
            ("not-signed-by-correct-person", false) => mappings.NotSignedByCorrectPersonEn,
            ("not-signed-by-correct-person", true) => mappings.NotSignedByCorrectPersonCy,
            ("recycling-obligations-changed", false) => mappings.RecyclingObligationsChangedEn,
            ("recycling-obligations-changed", true) => mappings.RecyclingObligationsChangedCy,
            ("can-meet-recycling-obligations", false) => mappings.CanMeetRecyclingObligationsEn,
            ("can-meet-recycling-obligations", true) => mappings.CanMeetRecyclingObligationsCy,
            ("producer-requested", false) => mappings.ProducerRequestedEn,
            ("producer-requested", true) => mappings.ProducerRequestedCy,
            _ => throw new InvalidOperationException("Cancellation family invalid"),
        };
    }
}
