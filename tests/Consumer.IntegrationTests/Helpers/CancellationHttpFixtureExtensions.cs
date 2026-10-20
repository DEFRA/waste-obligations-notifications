using System.Text;
using System.Text.Json;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
using Defra.WasteObligations.Consumer.Services.WasteOrganisations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using LiveOrganisation = Defra.WasteObligations.Consumer.Services.WasteOrganisations.Organisation;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Helpers;

public static class CancellationHttpFixtureExtensions
{
    public static async Task WaitForCancellationHost(this HttpClient client, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            using var response = await client.GetAsync("/health", deadline.Token);
            if (response.IsSuccessStatusCode)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
        }
    }

    public static void MapCancellationDependencies(this WebApplication app, CancellationHttpState state)
    {
        var expectedAuthorization =
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("isolated-reader:synthetic-read-secret"));

        app.MapGet(
            "/organisations/{organisationId:guid}/compliance-declarations/{declarationId}",
            (HttpContext context, Guid organisationId, string declarationId) =>
            {
                Assert.Equal(state.OrganisationId, organisationId);
                Assert.Equal(state.DeclarationId, declarationId);
                Assert.Equal(expectedAuthorization, context.Request.Headers.Authorization.ToString());
                var declaration = state.Declaration();
                var status = state.ReadStatus;
                Interlocked.Increment(ref state.DeclarationReads);

                return status == 200 ? Results.Json(declaration) : Results.StatusCode(status);
            }
        );
        app.MapGet(
            "/organisations/{organisationId:guid}",
            (HttpContext context, Guid organisationId) =>
            {
                Assert.Equal(state.OrganisationId, organisationId);
                Assert.Equal(expectedAuthorization, context.Request.Headers.Authorization.ToString());
                Interlocked.Increment(ref state.LiveReads);

                return Results.Json(new LiveOrganisation(state.OrganisationId, state.Country, "AB123456"));
            }
        );
        app.MapPost(
            "/token",
            async (HttpContext context) =>
            {
                var body = await context.Request.ReadFormAsync(context.RequestAborted);
                Assert.Equal("client_credentials", body["grant_type"].ToString());
                Assert.Equal("isolated-reader", body["client_id"].ToString());
                Assert.Equal("synthetic-read-secret", body["client_secret"].ToString());
                Interlocked.Increment(ref state.TokenReads);

                return Results.Json(new { access_token = state.AccessToken, expires_in = 3600 });
            }
        );
        app.MapPost(
            "/api/organisations/organisations-by-companies-house-numbers",
            async (HttpContext context) =>
            {
                Assert.Equal($"Bearer {state.AccessToken}", context.Request.Headers.Authorization.ToString());
                using var body = await JsonDocument.ParseAsync(
                    context.Request.Body,
                    cancellationToken: context.RequestAborted
                );
                Assert.Equal("AB123456", body.RootElement.GetProperty("companiesHouseNumbers")[0].GetString());
                Interlocked.Increment(ref state.SearchReads);

                return Results.Json(new[] { new AccountOrganisation(state.SchemeId.ToString(), "AB123456", true) });
            }
        );
        app.MapGet(
            "/api/organisations/organisation-with-persons/{organisationId:guid}",
            (HttpContext context, Guid organisationId) =>
            {
                Assert.Equal(state.Scheme ? state.SchemeId : state.OrganisationId, organisationId);
                Assert.Equal($"Bearer {state.AccessToken}", context.Request.Headers.Authorization.ToString());
                Interlocked.Increment(ref state.PersonReads);

                return Results.Json(new OrganisationWithPersons(state.Persons));
            }
        );
    }
}
