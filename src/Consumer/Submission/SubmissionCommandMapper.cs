using System.Text.Json;
using Defra.WasteObligations.Consumer.Commands;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Submission;

public sealed class SubmissionCommandMapper(IOptions<SubmissionEmailOptions> options)
{
    private const string InvalidDeclaration = "invalid-declaration";

    public NotificationCommand Map(SubmissionEvent submission, JsonElement declaration)
    {
        if (
            SubmissionEvent.ReadString(declaration, "id") != submission.DeclarationId
            || !declaration.TryGetProperty("organisation", out var organisation)
            || !Guid.TryParseExact(SubmissionEvent.ReadString(organisation, "id"), "D", out var organisationId)
            || organisationId != submission.OrganisationId
        )
            throw new SubmissionProcessingException("declaration-identity-mismatch");
        ValidateVersion(submission, declaration);
        if (!declaration.TryGetProperty("audit", out var audit) || audit.ValueKind != JsonValueKind.Array)
            throw new SubmissionProcessingException(InvalidDeclaration);
        var submitted = audit
            .EnumerateArray()
            .FirstOrDefault(entry => SubmissionEvent.ReadString(entry, "action") == "Submitted");
        if (
            submitted.ValueKind != JsonValueKind.Object
            || !submitted.TryGetProperty("user", out var user)
            || !UtcTimestamp.TryParse(SubmissionEvent.ReadString(submitted, "timestamp"), out var actionAt)
        )
            throw new SubmissionProcessingException(InvalidDeclaration);
        var recipient = Require(user, "email");
        var userName = Require(user, "name");
        var registrationType = Require(organisation, "registrationType");
        var regulator = Require(organisation, "regulator");
        var regulatorEmail = Require(organisation, "regulatorEmail");
        if (
            !declaration.TryGetProperty("obligationYear", out var year)
            || !year.TryGetInt32(out var obligationYear)
            || obligationYear <= 0
        )
            throw new SubmissionProcessingException(InvalidDeclaration);
        var scheme = registrationType == "ComplianceScheme";
        var welsh = SubmissionEvent.ReadString(organisation, "businessCountry") == "GB-WLS";
        var templateId = (scheme, welsh) switch
        {
            (false, false) => options.Value.DirectEnglishTemplateId,
            (false, true) => options.Value.DirectWelshTemplateId,
            (true, false) => options.Value.SchemeEnglishTemplateId,
            (true, true) => options.Value.SchemeWelshTemplateId,
        };
        var command = new NotificationCommand(
            NotificationCommand.CurrentSchemaVersion,
            $"submission:{organisationId:D}:{submission.DeclarationId}",
            actionAt,
            $"submitted-{(scheme ? "scheme" : "direct")}-{(welsh ? "cy" : "en")}",
            recipient,
            templateId,
            JsonSerializer.SerializeToElement(
                new
                {
                    obligationYear,
                    regulatorLeading = welsh ? regulator : "The " + regulator,
                    regulatorInline = welsh ? regulator : "the " + regulator,
                    regulatorEmail,
                    user = userName,
                }
            )
        );
        command.Validate();

        return command;
    }

    private static void ValidateVersion(SubmissionEvent submission, JsonElement declaration)
    {
        if (
            !declaration.TryGetProperty("version", out var version)
            || !version.TryGetInt32(out var number)
            || number <= 0
        )
            throw new SubmissionProcessingException(InvalidDeclaration);
        if (number < submission.Version)
            throw new SubmissionProcessingException("older-declaration-version");
    }

    private static string Require(JsonElement element, string property)
    {
        var value = SubmissionEvent.ReadString(element, property);
        if (string.IsNullOrWhiteSpace(value))
            throw new SubmissionProcessingException(InvalidDeclaration);

        return value;
    }
}
