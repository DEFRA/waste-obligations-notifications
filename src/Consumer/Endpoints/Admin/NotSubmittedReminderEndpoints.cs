using Defra.WasteObligations.Consumer.Scheduled;
using Microsoft.AspNetCore.Mvc;

namespace Defra.WasteObligations.Consumer.Endpoints.Admin;

public static class NotSubmittedReminderEndpoints
{
    public static void MapNotSubmittedReminderEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/not-submitted-reminder/scan", TriggerScan).ExcludeFromDescription();
        builder.MapGet("/not-submitted-reminder/status", GetStatus).ExcludeFromDescription();
    }

    private static async Task<IResult> TriggerScan(
        [FromServices] NotSubmittedReminderScanTrigger trigger,
        CancellationToken cancellationToken,
        [FromQuery] bool reopen = false
    )
    {
        var skipReason = await trigger.Request(reopen, cancellationToken);
        if (skipReason is not null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                detail: $"Not submitted reminder scan was not started ({skipReason})."
            );
        }

        return Results.Accepted();
    }

    private static async Task<IResult> GetStatus(
        [FromServices] INotSubmittedReminderScanService scanService,
        CancellationToken cancellationToken
    ) => Results.Ok(await scanService.GetRunStatus(cancellationToken));
}
