namespace Defra.WasteObligations.Consumer.Administration;

public enum CommandDlqDiscardResult
{
    Discarded,
    InvalidSelection,
    Conflict,
}
