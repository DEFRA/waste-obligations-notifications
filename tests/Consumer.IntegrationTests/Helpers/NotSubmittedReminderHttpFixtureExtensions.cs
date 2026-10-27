using System.Text;
using Defra.WasteObligations.Consumer.Services.AccountBackend;
using Defra.WasteObligations.Consumer.Services.WasteObligations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Defra.WasteObligations.Consumer.IntegrationTests.Helpers;

public static class NotSubmittedReminderHttpFixtureExtensions
{
    public static void MapNotSubmittedReminderDependencies(this WebApplication app, NotSubmittedReminderHttpState state)
    {
        var expectedAuthorization =
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("isolated-reader:synthetic-read-secret"));

        app.MapGet(
            "/compliance-declarations/unsubmitted",
            (HttpContext context, int obligationYear, string registrationType, int page, int pageSize) =>
            {
                Assert.Equal(expectedAuthorization, context.Request.Headers.Authorization.ToString());
                Assert.Equal(state.ObligationYear, obligationYear);
                Assert.Equal("DirectProducer", registrationType);
                Interlocked.Increment(ref state.UnsubmittedReads);
                var organisations = state.Organisations;

                return Results.Json(
                    new UnsubmittedOrganisationsPage(
                        organisations.Skip((page - 1) * pageSize).Take(pageSize).ToArray(),
                        organisations.Count,
                        page,
                        pageSize
                    )
                );
            }
        );
        app.MapPost("/token", () => Results.Json(new { access_token = state.AccessToken, expires_in = 3600 }));
        app.MapGet(
            "/api/organisations/organisation-with-persons/{organisationId:guid}",
            (HttpContext context, Guid organisationId) =>
            {
                Assert.Equal($"Bearer {state.AccessToken}", context.Request.Headers.Authorization.ToString());

                return state.Persons.TryGetValue(organisationId, out var persons)
                    ? Results.Json(new OrganisationWithPersons(persons))
                    : Results.NotFound();
            }
        );
    }
}
