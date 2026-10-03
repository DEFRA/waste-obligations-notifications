namespace Defra.WasteObligations.Consumer.Administration;

public enum CommandDlqRedriveResult
{
    Redriven,
    InvalidSelection,
    SelectionUnavailable,
}
