namespace Defra.WasteObligations.Consumer.Commands;

public interface INotificationCommandPublisher
{
    Task Publish(NotificationCommand command, CancellationToken cancellationToken);
}
