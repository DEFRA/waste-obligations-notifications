using System.Text.Json;
using Defra.WasteObligations.Consumer.Commands;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Submission;

public sealed class SubmittedEmailProducer(IWasteObligationsClient reader, IOptions<SubmissionOptions> options)
{
    private const string InvalidDeclarationData = "invalid-declaration-data";

    public async Task<Declaration> Read(
        Guid organisationId,
        string declarationId,
        int eventVersion,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var declaration = await reader.ReadDeclaration(organisationId, declarationId, cancellationToken);
            if (declaration is null)
                throw new SubmissionProcessingException("declaration-not-found");

            var organisation =
                declaration.Organisation ?? throw new SubmissionProcessingException(InvalidDeclarationData);
            if (Required(declaration.Id) != declarationId || Guid.Parse(Required(organisation.Id)) != organisationId)
                throw new SubmissionProcessingException("identity-mismatch");

            if (declaration.Version is null)
                throw new SubmissionProcessingException(InvalidDeclarationData);

            if (declaration.Version < eventVersion)
                throw new SubmissionProcessingException("older-declaration-version");

            return declaration;
        }
        catch (SubmissionProcessingException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception
                    is JsonException
                        or InvalidOperationException
                        or KeyNotFoundException
                        or FormatException
                        or InvalidDataException
                        or OverflowException
            )
        {
            throw new SubmissionProcessingException(InvalidDeclarationData);
        }
    }

    public async Task<NotificationCommand> Create(
        Guid organisationId,
        string declarationId,
        int eventVersion,
        CancellationToken cancellationToken
    )
    {
        var declaration = await Read(organisationId, declarationId, eventVersion, cancellationToken);

        return Create(declaration);
    }

    public NotificationCommand Create(Declaration declaration)
    {
        try
        {
            var organisation =
                declaration.Organisation ?? throw new SubmissionProcessingException(InvalidDeclarationData);
            var declarationId = Required(declaration.Id);
            var submitted =
                declaration.Audit?.First(entry => entry.Action == "Submitted")
                ?? throw new SubmissionProcessingException(InvalidDeclarationData);
            var user = submitted.User ?? throw new SubmissionProcessingException(InvalidDeclarationData);
            if (!UtcTimestamp.TryParse(Required(submitted.Timestamp), out var actionTime))
                throw new SubmissionProcessingException(InvalidDeclarationData);

            var year = declaration.ObligationYear;
            if (year <= 0)
                throw new SubmissionProcessingException(InvalidDeclarationData);

            var welsh = organisation.BusinessCountry == "GB-WLS";
            var scheme = Required(organisation.RegistrationType) == "ComplianceScheme";
            var regulator = Required(organisation.Regulator);
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
                Required(user.Email),
                template,
                JsonSerializer.SerializeToElement(
                    new
                    {
                        obligationYear = year,
                        regulatorLeading = welsh ? regulator : $"The {regulator}",
                        regulatorInline = welsh ? regulator : $"the {regulator}",
                        regulatorEmail = Required(organisation.RegulatorEmail),
                        user = Required(user.Name),
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

    private static string Required(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new SubmissionProcessingException(InvalidDeclarationData);

        return value;
    }
}
