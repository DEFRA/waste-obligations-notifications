using System.Net.Http.Headers;
using System.Text;

namespace Defra.WasteObligations.Consumer.Services.Http;

public static class BasicAuthenticationExtensions
{
    public static AuthenticationHeaderValue CreateAuthorizationHeader(this BasicAuthenticationOptions options) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}")));
}
