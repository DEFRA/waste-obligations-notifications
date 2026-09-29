using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Defra.WasteObligations.Consumer.Utils;

[ExcludeFromCodeCoverage]
public static class TrustStore
{
    public static void LoadCustomTrustStoreFromEnvironment(this IServiceCollection _)
    {
        var certificates = Environment
            .GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .Where(entry => entry.Key.ToString()!.StartsWith("TRUSTSTORE_", StringComparison.Ordinal))
            .Select(entry => entry.Value?.ToString() ?? "")
            .Where(IsBase64String)
            .Select(Convert.FromBase64String)
            .Select(X509CertificateLoader.LoadCertificate)
            .ToList();

        if (certificates.Count == 0)
        {
            return;
        }

        var collection = new X509Certificate2Collection();
        collection.AddRange(certificates.ToArray());

        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.AddRange(collection);
    }

    private static bool IsBase64String(string value)
    {
        var buffer = new Span<byte>(new byte[value.Length]);
        return Convert.TryFromBase64String(value, buffer, out _);
    }
}
