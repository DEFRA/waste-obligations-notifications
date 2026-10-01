namespace Defra.WasteObligations.Consumer.Delivery;

public interface INotificationCommandMetrics
{
    void RecordReceived(string notificationType);
    void RecordOutcome(string notificationType, string outcome);
    void RecordClaim(string notificationType, DeliveryClaimResult result, double elapsedMilliseconds);
    void RecordClaimFailure(string notificationType, double elapsedMilliseconds);
    void RecordSendAccepted(string notificationType);
    void RecordSendFailure(string notificationType);
    void RecordSendDuration(string notificationType, double elapsedMilliseconds);
    void RecordDuplicate(string notificationType);
}
