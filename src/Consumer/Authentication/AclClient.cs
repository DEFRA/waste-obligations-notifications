namespace Defra.WasteObligations.Consumer.Authentication;

public sealed record AclClient
{
    public ClientType? Type { get; init; }

    public string? Secret { get; init; }

    public string[] Scopes { get; init; } = [];
}
