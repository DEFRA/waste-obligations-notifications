namespace Defra.WasteObligations.Consumer.Administration;

public sealed record CommandDlqSelectionRequest(
    string? SelectionToken = null,
    IReadOnlyList<string?>? SelectionTokens = null
);
