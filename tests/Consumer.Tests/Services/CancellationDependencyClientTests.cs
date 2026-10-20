using System.Net;
using System.Text.Json;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteOrganisations;
using Defra.WasteObligations.Consumer.Tests.Helpers;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Defra.WasteObligations.Consumer.Tests.Services;

public sealed class CancellationDependencyClientTests
{
    [Fact]
    public async Task WhenSearchingSchemes_ShouldAuthenticateAndUseExpectedContract()
    {
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "/api/organisations/organisations-by-companies-house-numbers",
                request.RequestUri!.AbsolutePath
            );
            Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("12345678", body.RootElement.GetProperty("companiesHouseNumbers")[0].GetString());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"externalId\":\"account-id\",\"companiesHouseNumber\":\"12345678\",\"isComplianceScheme\":true}]"
                ),
            };
        };
        using var http = send.CreateHttpStub();
        http.BaseAddress = new Uri("http://localhost/");
        using var tokens = CreateTokens();
        var client = new AccountBackendClient(http, tokens);

        var result = Assert.Single(
            await client.SearchOrganisationsByCompaniesHouseNumbers(["12345678"], TestContext.Current.CancellationToken)
        );

        Assert.Equal("account-id", result.ExternalId);
        Assert.True(result.IsComplianceScheme);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "null", false)]
    [InlineData(HttpStatusCode.NotFound, "", false)]
    [InlineData(HttpStatusCode.Forbidden, "private response", true)]
    public async Task WhenReadingPersons_ShouldDistinguishEmptyFromFailure(
        HttpStatusCode status,
        string body,
        bool failure
    )
    {
        var id = Guid.NewGuid();
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (request, _) =>
        {
            Assert.Equal($"/api/organisations/organisation-with-persons/{id:D}", request.RequestUri!.AbsolutePath);

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        };
        using var http = send.CreateHttpStub();
        http.BaseAddress = new Uri("http://localhost/");
        using var tokens = CreateTokens();
        var client = new AccountBackendClient(http, tokens);
        if (failure)
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.ReadOrganisationWithPersons(id, TestContext.Current.CancellationToken)
            );
        else
            Assert.Null(await client.ReadOrganisationWithPersons(id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenSearchResponseIsNull_ShouldFailInsteadOfReturningNoMatches()
    {
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null") });
        using var http = send.CreateHttpStub();
        http.BaseAddress = new Uri("http://localhost/");
        using var tokens = CreateTokens();
        var client = new AccountBackendClient(http, tokens);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SearchOrganisationsByCompaniesHouseNumbers(["12345678"], TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task WhenReadingLiveOrganisation_ShouldUseBasicCredentialsAndTypedFields()
    {
        var id = Guid.NewGuid();
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (request, _) =>
        {
            Assert.Equal($"/api/organisations/{id:D}", request.RequestUri!.AbsolutePath);
            Assert.Equal("Basic dXNlcjpzZWNyZXQ=", request.Headers.Authorization!.ToString());

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{{\"id\":\"{id:D}\",\"businessCountry\":\"GB-WLS\",\"companiesHouseNumber\":\"12345678\"}}"
                    ),
                }
            );
        };
        using var http = send.CreateHttpStub();
        var client = new WasteOrganisationsClient(
            http,
            Options.Create(new WasteOrganisationsOptions { Username = "user", Password = "secret" })
        );

        var result = await client.Read(id, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(id, result.Id);
        Assert.Equal("GB-WLS", result.BusinessCountry);
        Assert.Equal("12345678", result.CompaniesHouseNumber);
    }

    [Fact]
    public async Task WhenCheckingDependencies_ShouldAuthenticateExpectedHealthPaths()
    {
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (request, token) =>
        {
            Assert.True(token.CanBeCanceled);
            if (request.RequestUri!.AbsolutePath == "/admin/health")
                Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
            else
            {
                Assert.Equal("/health/authorized", request.RequestUri.AbsolutePath);
                Assert.Equal("Basic dXNlcjpzZWNyZXQ=", request.Headers.Authorization!.ToString());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        };
        using var http = send.CreateHttpStub();
        http.BaseAddress = new Uri("http://localhost/");
        using var tokens = CreateTokens();
        var account = new AccountBackendClient(http, tokens);
        var organisations = new WasteOrganisationsClient(
            http,
            Options.Create(new WasteOrganisationsOptions { Username = "user", Password = "secret" })
        );

        await account.CheckHealth(TestContext.Current.CancellationToken);
        await organisations.CheckHealth(TestContext.Current.CancellationToken);
    }

    private static AccountTokenProvider CreateTokens()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory
            .CreateClient(AccountTokenProvider.HttpClientName)
            .Returns(_ =>
            {
                Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (_, _) =>
                    Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"access_token\":\"test-token\",\"expires_in\":3600}"),
                        }
                    );

                return send.CreateHttpStub();
            });

        return new AccountTokenProvider(
            factory,
            Options.Create(new AccountBackendOptions { TokenEndpoint = "http://localhost/token" }),
            TimeProvider.System
        );
    }
}
