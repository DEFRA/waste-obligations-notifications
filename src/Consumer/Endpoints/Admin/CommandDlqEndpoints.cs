using Defra.WasteObligations.Consumer.Administration;

namespace Defra.WasteObligations.Consumer.Endpoints.Admin;

public static class CommandDlqEndpoints
{
    public static void MapCommandDlqEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/notification-commands/dlq/inspect", Inspect).ExcludeFromDescription();
        builder.MapPost("/notification-commands/dlq/redrive", Redrive).ExcludeFromDescription();
    }

    private static async Task<IResult> Redrive(
        CommandDlqSelectionRequest request,
        CommandDlqRedriver redriver,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await redriver.Redrive(request.SelectionToken, cancellationToken);

            return result switch
            {
                CommandDlqRedriveResult.Redriven => Results.NoContent(),
                CommandDlqRedriveResult.InvalidSelection => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    detail: "Command DLQ selection is invalid or expired."
                ),
                _ => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    detail: "Selected command is no longer valid or available."
                ),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Command DLQ redrive failed."
            );
        }
    }

    private static async Task<IResult> Inspect(CommandDlqInspector inspector, CancellationToken cancellationToken)
    {
        try
        {
            var inspection = await inspector.Inspect(cancellationToken);

            return inspection is null ? Results.NoContent() : Results.Ok(inspection);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Command DLQ inspection failed."
            );
        }
    }
}
