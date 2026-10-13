namespace Defra.WasteObligations.Consumer.Administration;

public sealed record CommandDlqInspectionLinks(string DeliveryRecord, string Resends);
