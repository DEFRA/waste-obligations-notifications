using Defra.WasteObligations.Consumer.Commands;

namespace Defra.WasteObligations.Consumer.Delivery;

public sealed record NotifyAcceptance(string NotificationId, string Reference, string TemplateId, int TemplateVersion)
{
    public bool Matches(NotificationCommand command, string expectedReference) =>
        Guid.TryParse(NotificationId, out _)
        && Reference == expectedReference
        && TemplateVersion > 0
        && (
            Guid.TryParse(command.TemplateId, out var requestedTemplate)
            && Guid.TryParse(TemplateId, out var acceptedTemplate)
                ? requestedTemplate == acceptedTemplate
                : string.Equals(command.TemplateId, TemplateId, StringComparison.Ordinal)
        );
}
