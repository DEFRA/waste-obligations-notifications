using Defra.WasteObligations.Consumer.Administration;
using Defra.WasteObligations.Consumer.Endpoints.Admin;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Consumer.Endpoints;

public static class Endpoints
{
    public static void MapApiEndpoints(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<CommandDlqAdministrationOptions>>().Value.Enabled)
            app.MapAdminEndpoints();
    }
}
