using System.ComponentModel.DataAnnotations;

namespace Defra.WasteObligations.Consumer.Authentication;

public sealed record AclOptions
{
    public const string SectionName = "Acl";

    [Required]
    public Dictionary<string, AclClient> Clients { get; init; } = [];

    public bool HasValidClients =>
        Clients is not null
        && Clients.All(entry =>
            !string.IsNullOrWhiteSpace(entry.Key)
            && !entry.Key.Contains(':', StringComparison.Ordinal)
            && !entry.Key.Any(char.IsControl)
            && entry.Value is { Type: ClientType.ApiKey or ClientType.OAuth, Scopes: not null }
            && (entry.Value.Type == ClientType.OAuth || IsConfiguredSecret(entry.Value.Secret))
            && entry.Value.Scopes.All(scope => !string.IsNullOrWhiteSpace(scope) && !scope.Any(char.IsControl))
        );

    private static bool IsConfiguredSecret(string? secret) =>
        !string.IsNullOrWhiteSpace(secret) && !secret.StartsWith("set-automatically", StringComparison.Ordinal);
}
