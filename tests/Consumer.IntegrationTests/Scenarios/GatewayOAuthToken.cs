using System.Text;
using System.Text.Json;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Scenarios;

internal static class GatewayOAuthToken
{
    // The real gateway validates signatures. Local tokens exercise the approved backend contract, not Cognito.
    public static string Create(string clientId)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                client_id = clientId,
                scope = "service-resource-srv/access",
                exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
            }
        );

        return $"Bearer {Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{Encode(payload)}.";
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
