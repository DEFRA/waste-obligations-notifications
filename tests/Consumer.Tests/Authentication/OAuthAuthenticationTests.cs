using System.Net;
using System.Text;
using System.Text.Json;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests.Authentication;

public sealed class OAuthAuthenticationTests
{
    [Fact]
    public async Task WhenGatewayTokenIdentifiesOAuthAdmin_ShouldUseOnlyAclClaims()
    {
        await using var fixture = await BasicAuthenticationTests.AuthenticationFixture.Create();
        var token = Token("oauth");
        using var request = Request(token);

        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal("oauth", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("oauth", body.RootElement.GetProperty("clientId").GetString());
        Assert.Equal("Bearer", body.RootElement.GetProperty("authenticationType").GetString());
        Assert.Equal(
            ["admin", "read"],
            body.RootElement.GetProperty("scopes").EnumerateArray().Select(value => value.GetString())
        );
        Assert.Equal(1, fixture.EndpointCalls);
        Assert.Single(fixture.Sqs.ReceivedCalls());
        Assert.DoesNotContain(
            fixture.Logs.Messages,
            message => message.Contains("Basic was not authenticated", StringComparison.Ordinal)
        );
        Assert.All(fixture.Logs.Messages, message => Assert.DoesNotContain(token, message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("malformed", 401)]
    [InlineData("missing-client", 401)]
    [InlineData("empty-client", 401)]
    [InlineData("duplicate-client", 401)]
    [InlineData("unknown-client", 401)]
    [InlineData("apikey-client", 401)]
    [InlineData("expired", 401)]
    [InlineData("future", 401)]
    [InlineData("missing-expiry", 401)]
    [InlineData("multiple-headers", 401)]
    [InlineData("acl-read-token-admin", 403)]
    public async Task WhenBearerCallerIsNotAnAclAdmin_ShouldDenyWithoutEffectsOrTokenDisclosure(
        string condition,
        int expected
    )
    {
        await using var fixture = await BasicAuthenticationTests.AuthenticationFixture.Create();
        var clientId = condition switch
        {
            "missing-client" => null,
            "empty-client" => "",
            "unknown-client" => "unknown",
            "apikey-client" => "admin",
            "acl-read-token-admin" => "oauth-read",
            _ => "oauth",
        };
        var token = condition == "malformed" ? "private-invalid-token" : Token(clientId, condition);
        using var request = Request(token);
        if (condition == "multiple-headers")
        {
            request.Headers.Remove("Authorization");
            request.Headers.TryAddWithoutValidation("Authorization", new[] { $"Bearer {token}", $"Bearer {token}" });
        }

        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.Equal(0, fixture.EndpointCalls);
        Assert.Empty(fixture.Sqs.ReceivedCalls());
        Assert.Empty(fixture.Store.ReceivedCalls());
        Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token-content", body, StringComparison.Ordinal);
        Assert.All(
            fixture.Logs.Messages,
            message =>
            {
                Assert.DoesNotContain(token, message, StringComparison.Ordinal);
                Assert.DoesNotContain("private-token-content", message, StringComparison.Ordinal);
            }
        );
    }

    private static HttpRequestMessage Request(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/authentication-probe");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        return request;
    }

    // Synthetic unsigned tokens exercise the explicitly approved gateway-owned validation contract.
    private static string Token(string? clientId, string condition = "valid")
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object>
        {
            ["iss"] = "https://gateway-validated-issuer.example",
            ["scope"] = "admin",
            ["role"] = "admin",
            ["private"] = "private-token-content",
            ["exp"] = now.AddMinutes(condition == "expired" ? -10 : 30).ToUnixTimeSeconds(),
            ["nbf"] = now.AddMinutes(condition == "future" ? 10 : -30).ToUnixTimeSeconds(),
        };
        if (clientId is not null)
            payload["client_id"] = condition == "duplicate-client" ? new[] { clientId, clientId } : clientId;
        if (condition == "missing-expiry")
            payload.Remove("exp");

        return $"{Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{Encode(JsonSerializer.Serialize(payload))}.";
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
