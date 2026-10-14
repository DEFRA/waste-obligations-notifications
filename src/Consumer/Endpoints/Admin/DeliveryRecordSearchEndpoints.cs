using Defra.WasteObligations.Consumer.Administration;

namespace Defra.WasteObligations.Consumer.Endpoints.Admin;

public static class DeliveryRecordSearchEndpoints
{
    public static void MapDeliveryRecordSearchEndpoints(this IEndpointRouteBuilder builder) =>
        builder.MapGet("/notification-commands/delivery-records", Search).ExcludeFromDescription();

    private static async Task<IResult> Search(
        HttpRequest request,
        IDeliveryRecordSearchReader reader,
        CommandDlqDiagnostics diagnostics,
        CancellationToken cancellationToken
    )
    {
        DeliveryRecordSearchQuery query;
        try
        {
            query = DeliveryRecordSearchQuery.Parse(request.Query);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "Delivery record search query is invalid."
            );
        }
        try
        {
            return Results.Ok(await reader.Search(query, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            diagnostics.SearchFailed();

            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Delivery record search failed."
            );
        }
    }
}
