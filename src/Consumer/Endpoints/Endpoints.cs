using Defra.WasteObligations.Consumer.Endpoints.Admin;

namespace Defra.WasteObligations.Consumer.Endpoints;

public static class Endpoints
{
    public static void MapApiEndpoints(this WebApplication app)
    {
        app.MapAdminEndpoints();
    }
}
