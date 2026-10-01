using Defra.WasteObligations.Consumer.Administration;

namespace Defra.WasteObligations.Consumer.Endpoints.Admin;

public static class CommandDlqEndpoints
{
    public static void MapCommandDlqEndpoints(this IEndpointRouteBuilder builder) =>
        builder.MapPost("/notification-commands/dlq/inspect", Inspect).ExcludeFromDescription();

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
