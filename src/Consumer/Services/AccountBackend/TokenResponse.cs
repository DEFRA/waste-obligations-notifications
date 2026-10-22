namespace Defra.WasteObligations.Consumer.Services.AccountBackend;

public sealed record TokenResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string? AccessToken,
    [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int ExpiresIn
);
