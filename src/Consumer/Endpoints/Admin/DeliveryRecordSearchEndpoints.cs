using Defra.WasteObligations.Consumer.Administration;
using Defra.WasteObligations.Consumer.Delivery;

namespace Defra.WasteObligations.Consumer.Endpoints.Admin;

public static class DeliveryRecordSearchEndpoints
{
    public static void MapDeliveryRecordSearchEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("/notification-commands/delivery-records", SearchAll).ExcludeFromDescription();
        builder.MapPost("/notification-commands/delivery-records", SearchByRecipient).ExcludeFromDescription();
    }

    private sealed record RecipientSearch(string Recipient);

    private static Task<IResult> SearchAll(
        HttpRequest request,
        IDeliveryRecordSearchReader reader,
        CommandDlqDiagnostics diagnostics,
        CancellationToken cancellationToken
    ) => Search(request, reader, diagnostics, null, cancellationToken);

    private static Task<IResult> SearchByRecipient(
        HttpRequest request,
        RecipientSearch input,
        INotificationCommandDigest digest,
        IDeliveryRecordSearchReader reader,
        CommandDlqDiagnostics diagnostics,
        CancellationToken cancellationToken
    )
    {
        if (
            string.IsNullOrWhiteSpace(input.Recipient)
            || input.Recipient.Length > 320
            || input.Recipient.Any(char.IsControl)
        )
            return Task.FromResult<IResult>(
                Results.Problem(statusCode: 400, detail: "Delivery record search query is invalid.")
            );

        return Search(request, reader, diagnostics, digest.CreateRecipientDigest(input.Recipient), cancellationToken);
    }

    private static async Task<IResult> Search(
        HttpRequest request,
        IDeliveryRecordSearchReader reader,
        CommandDlqDiagnostics diagnostics,
        string? recipientDigest,
        CancellationToken cancellationToken
    )
    {
        DeliveryRecordSearchQuery query;
        try
        {
            query = DeliveryRecordSearchQuery.Parse(request.Query, recipientDigest);
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
