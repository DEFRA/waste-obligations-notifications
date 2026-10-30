using Defra.WasteObligations.Consumer.Scheduled;
using Microsoft.AspNetCore.Mvc;

namespace Defra.WasteObligations.Consumer.Endpoints.Admin;

public static class NotSubmittedReminderEndpoints
{
    public static void MapNotSubmittedReminderEndpoints(this IEndpointRouteBuilder builder)
    {
        builder
            .MapPost(
                "/not-submitted-reminder/scan",
                (
                    [FromKeyedServices(NotSubmittedReminderCampaign.Email1Id)] NotSubmittedReminderScanTrigger trigger,
                    CancellationToken cancellationToken,
                    [FromQuery] bool reopen = false
                ) => TriggerScan(trigger, reopen, cancellationToken)
            )
            .ExcludeFromDescription();
        builder
            .MapGet(
                "/not-submitted-reminder/status",
                (
                    [FromKeyedServices(NotSubmittedReminderCampaign.Email1Id)]
                        INotSubmittedReminderScanService scanService,
                    CancellationToken cancellationToken
                ) => GetStatus(scanService, cancellationToken)
            )
            .ExcludeFromDescription();

        builder
            .MapPost(
                "/not-submitted-reminder-2/scan",
                (
                    [FromKeyedServices(NotSubmittedReminderCampaign.Email2Id)] NotSubmittedReminderScanTrigger trigger,
                    CancellationToken cancellationToken,
                    [FromQuery] bool reopen = false
                ) => TriggerScan(trigger, reopen, cancellationToken)
            )
            .ExcludeFromDescription();
        builder
            .MapGet(
                "/not-submitted-reminder-2/status",
                (
                    [FromKeyedServices(NotSubmittedReminderCampaign.Email2Id)]
                        INotSubmittedReminderScanService scanService,
                    CancellationToken cancellationToken
                ) => GetStatus(scanService, cancellationToken)
            )
            .ExcludeFromDescription();
    }

    private static async Task<IResult> TriggerScan(
        NotSubmittedReminderScanTrigger trigger,
        bool reopen,
        CancellationToken cancellationToken
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
        INotSubmittedReminderScanService scanService,
        CancellationToken cancellationToken
    ) => Results.Ok(await scanService.GetRunStatus(cancellationToken));
}
