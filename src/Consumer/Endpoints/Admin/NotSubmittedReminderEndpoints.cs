using Defra.WasteObligations.Consumer.Scheduled;

namespace Defra.WasteObligations.Consumer.Endpoints.Admin;

public static class NotSubmittedReminderEndpoints
{
    public static void MapNotSubmittedReminderEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/not-submitted-reminder/scan", TriggerScan).ExcludeFromDescription();
    }

    private static IResult TriggerScan(NotSubmittedReminderScanTrigger trigger)
    {
        var skipReason = trigger.Request();
        if (skipReason is not null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                detail: $"Not submitted reminder scan was not started ({skipReason})."
            );
        }

        return Results.Accepted();
    }
}
