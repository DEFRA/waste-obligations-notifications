namespace Defra.WasteObligations.Consumer.Delivery;

public interface INotificationDeliveryRecordStoreFactory
{
    INotificationDeliveryRecordStore GetRecordStore();
}
