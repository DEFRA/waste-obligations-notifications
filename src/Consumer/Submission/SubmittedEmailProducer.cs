using System.Text.Json;
using Defra.WasteObligations.Consumer.Commands;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Submission;

public sealed class SubmittedEmailProducer(IDeclarationReadClient reader, IOptions<SubmissionOptions> options)
{
    public async Task<NotificationCommand> Create(
        Guid organisationId,
        string declarationId,
        int eventVersion,
        CancellationToken cancellationToken
    )
    {
        var declaration = await reader.Read(organisationId, declarationId, cancellationToken);
        try
        {
            var organisation = declaration.GetProperty("organisation");
            if (
                Required(declaration, "id") != declarationId
                || Guid.Parse(Required(organisation, "id")) != organisationId
            )
                throw new SubmissionProcessingException("identity-mismatch");
            if (declaration.GetProperty("version").GetInt32() < eventVersion)
                throw new SubmissionProcessingException("older-declaration-version");
            var submitted = declaration
                .GetProperty("audit")
                .EnumerateArray()
                .First(entry => Required(entry, "action") == "Submitted");
            var user = submitted.GetProperty("user");
            if (!UtcTimestamp.TryParse(Required(submitted, "timestamp"), out var actionTime))
                throw new SubmissionProcessingException("invalid-declaration-data");
            var year = declaration.GetProperty("obligationYear").GetInt32();
            if (year <= 0)
                throw new SubmissionProcessingException("invalid-declaration-data");
            var welsh = Required(organisation, "businessCountry") == "GB-WLS";
            var scheme = Required(organisation, "registrationType") == "ComplianceScheme";
            var regulator = Required(organisation, "regulator");
            var template = (scheme, welsh) switch
            {
                (false, false) => options.Value.DirectEnglishTemplateId,
                (false, true) => options.Value.DirectWelshTemplateId,
                (true, false) => options.Value.SchemeEnglishTemplateId,
                (true, true) => options.Value.SchemeWelshTemplateId,
            };
            var command = new NotificationCommand(
                NotificationCommand.CurrentSchemaVersion,
                $"compliance-declaration:{declarationId}:submitted:v1",
                actionTime,
                $"declaration-submitted-{(scheme ? "scheme" : "direct")}-{(welsh ? "cy" : "en")}",
                Required(user, "email"),
                template,
                JsonSerializer.SerializeToElement(
                    new
                    {
                        obligationYear = year,
                        regulatorLeading = welsh ? regulator : $"The {regulator}",
                        regulatorInline = welsh ? regulator : $"the {regulator}",
                        regulatorEmail = Required(organisation, "regulatorEmail"),
                        user = Required(user, "name"),
                    }
                )
            ).NormaliseRecipient();
            command.Validate();

            return command;
        }
        catch (SubmissionProcessingException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception
                    is InvalidOperationException
                        or KeyNotFoundException
                        or FormatException
                        or InvalidDataException
                        or OverflowException
            )
        {
            throw new SubmissionProcessingException("invalid-declaration-data");
        }
    }

    private static string Required(JsonElement element, string name)
    {
        var value = element.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new SubmissionProcessingException("invalid-declaration-data");

        return value;
    }
}
