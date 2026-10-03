using Defra.WasteObligations.Consumer.Commands;

namespace Defra.WasteObligations.Consumer.Delivery;

public interface INotificationCommandDigest
{
    string CreateIdempotencyKeyDigest(string idempotencyKey);

    string CreateImmutableFieldsDigest(NotificationCommand command);

    string CreateRecipientDigest(string emailAddress);

    string CreateRecipientLane(string emailAddress);
}
