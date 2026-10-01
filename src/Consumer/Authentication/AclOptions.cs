using System.ComponentModel.DataAnnotations;

namespace Defra.WasteObligations.Consumer.Authentication;

public sealed record AclOptions
{
    public const string SectionName = "Acl";

    [Required]
    public Dictionary<string, AclClient> Clients { get; init; } = [];

    public bool HasConfiguredAdmin =>
        Clients is { Count: > 0 }
        && Clients.All(entry =>
            !string.IsNullOrWhiteSpace(entry.Key)
            && !entry.Key.Contains(':', StringComparison.Ordinal)
            && !entry.Key.Any(char.IsControl)
            && entry.Value is { Type: ClientType.ApiKey or ClientType.OAuth, Scopes: not null }
            && (entry.Value.Type == ClientType.OAuth || IsConfiguredSecret(entry.Value.Secret))
            && entry.Value.Scopes.All(scope => !string.IsNullOrWhiteSpace(scope) && !scope.Any(char.IsControl))
        )
        && Clients.Values.Any(client => client.Scopes.Contains(Scopes.Admin, StringComparer.Ordinal));

    private static bool IsConfiguredSecret(string? secret) =>
        !string.IsNullOrWhiteSpace(secret) && !secret.StartsWith("set-automatically", StringComparison.Ordinal);
}
