using Defra.WasteObligations.Consumer.Commands;

namespace Defra.WasteObligations.Consumer.Delivery;

public interface INotifyEmailClient
{
    Task CheckHealth(CancellationToken cancellationToken);

    Task<NotifyAcceptance> Send(NotificationCommand command, string reference, CancellationToken cancellationToken);
}
