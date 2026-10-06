using Defra.WasteObligations.Consumer.Authentication;
using Defra.WasteObligations.Consumer.Startup;

namespace Defra.WasteObligations.Consumer.Endpoints.Admin;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/admin").RequireAuthorization(PolicyNames.Admin);
        group.AddEndpointFilter(
            async (context, next) =>
                context.HttpContext.RequestServices.GetRequiredService<ApplicationStartup>().IsStarted
                    ? await next(context)
                    : Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        detail: "Application startup is incomplete."
                    )
        );
        group.MapCommandDlqEndpoints();
    }
}
