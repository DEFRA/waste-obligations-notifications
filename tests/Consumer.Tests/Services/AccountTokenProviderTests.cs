using System.Net;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Tests.Helpers;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests.Services;

public sealed class AccountTokenProviderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("account.read")]
    public async Task WhenConcurrentRequestsNeedToken_ShouldRequestOnceAndRefreshBeforeExpiry(string? scope)
    {
        var requests = 0;
        var clock = new Clock();
        var factory = Substitute.For<IHttpClientFactory>();
        factory
            .CreateClient(AccountTokenProvider.HttpClientName)
            .Returns(_ =>
            {
                Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = async (request, token) =>
                {
                    Interlocked.Increment(ref requests);
                    Assert.Equal("/token", request.RequestUri!.AbsolutePath);
                    var body = await request.Content!.ReadAsStringAsync(token);
                    Assert.Contains("grant_type=client_credentials", body);
                    Assert.Contains("client_id=client", body);
                    Assert.Contains("client_secret=secret", body);
                    if (scope is null)
                        Assert.DoesNotContain("scope=", body);
                    else
                        Assert.Contains("scope=account.read", body);

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"access_token\":\"cached-token\",\"expires_in\":120}"),
                    };
                };

                return send.CreateHttpStub();
            });
        using var provider = new AccountTokenProvider(
            factory,
            Options.Create(
                new AccountBackendOptions
                {
                    TokenEndpoint = "http://localhost/token",
                    ClientId = "client",
                    ClientSecret = "secret",
                    Scope = scope,
                }
            ),
            clock
        );

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => provider.GetToken(TestContext.Current.CancellationToken))
        );

        Assert.All(results, result => Assert.Equal("cached-token", result));
        Assert.Equal(1, requests);
        clock.Now = clock.Now.AddSeconds(60);
        await provider.GetToken(TestContext.Current.CancellationToken);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task WhenCallerIsCancelled_ShouldNotRequestToken()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        using var provider = new AccountTokenProvider(
            factory,
            Options.Create(new AccountBackendOptions()),
            TimeProvider.System
        );
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetToken(cancellation.Token));

        factory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
