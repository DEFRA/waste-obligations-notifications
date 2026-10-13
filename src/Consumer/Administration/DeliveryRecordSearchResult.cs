namespace Defra.WasteObligations.Consumer.Administration;

public sealed record DeliveryRecordSearchResult(IReadOnlyList<DeliveryRecordSummary> Records, string? NextCursor);
