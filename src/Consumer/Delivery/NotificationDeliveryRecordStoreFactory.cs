namespace Defra.WasteObligations.Consumer.Delivery;

public sealed class NotificationDeliveryRecordStoreFactory(IServiceProvider serviceProvider)
    : INotificationDeliveryRecordStoreFactory
{
    public INotificationDeliveryRecordStore GetRecordStore() =>
        serviceProvider.GetRequiredService<INotificationDeliveryRecordStore>();
}
