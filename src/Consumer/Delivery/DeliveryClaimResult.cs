namespace Defra.WasteObligations.Consumer.Delivery;

public enum DeliveryClaimResult
{
    Unavailable,
    Claimed,
    TerminalDuplicate,
    Conflict,
    ActiveClaim,
}
