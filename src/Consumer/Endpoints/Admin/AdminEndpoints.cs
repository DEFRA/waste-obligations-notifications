using Defra.WasteObligations.Consumer.Authentication;

namespace Defra.WasteObligations.Consumer.Endpoints.Admin;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/admin").RequireAuthorization(PolicyNames.Admin);
        group.MapCommandDlqEndpoints();
    }
}
