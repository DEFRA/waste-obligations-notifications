namespace Defra.WasteObligations.Consumer.Administration;

public interface IDeliveryRecordSearchReader
{
    Task<DeliveryRecordSearchResult> Search(DeliveryRecordSearchQuery query, CancellationToken cancellationToken);
}
