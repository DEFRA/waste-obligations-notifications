using Defra.WasteObligations.Consumer.Administration;
using Microsoft.AspNetCore.Http.Features;

namespace Defra.WasteObligations.Consumer.Endpoints.Admin;

public static class CommandDlqEndpoints
{
    public static void MapCommandDlqEndpoints(this IEndpointRouteBuilder builder)
    {
        builder
            .MapPost("/notification-commands/dlq/verification-command", CreateVerificationCommand)
            .ExcludeFromDescription();
        builder.MapPost("/notification-commands/dlq/inspect", Inspect).ExcludeFromDescription();
        builder.MapPost("/notification-commands/dlq/redrive", Redrive).ExcludeFromDescription();
        builder.MapPost("/notification-commands/dlq/discard", Discard).ExcludeFromDescription();
        builder.MapGet("/notification-commands/dlq/status", GetStatus).ExcludeFromDescription();
    }

    private static async Task<IResult> GetStatus(
        CommandDlqQueueOperations operations,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return Results.Ok(await operations.GetStatus(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Command DLQ status failed."
            );
        }
    }

    private static async Task<IResult> CreateVerificationCommand(
        HttpRequest request,
        CommandDlqVerificationCommandCreator creator,
        CancellationToken cancellationToken
    )
    {
        if (
            request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
            || request.ContentLength is > 0
            || request.Headers.ContainsKey("Transfer-Encoding")
        )
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "Verification command creation accepts no request body."
            );
        try
        {
            return Results.Ok(await creator.Create(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "Verification command creation failed."
            );
        }
    }

    private static async Task<IResult> Discard(
        CommandDlqSelectionRequest request,
        CommandDlqDiscarder discarder,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await discarder.Discard(request.SelectionToken, cancellationToken);

            return result switch
            {
                CommandDlqDiscardResult.Discarded => Results.NoContent(),
                CommandDlqDiscardResult.InvalidSelection => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    detail: "Command DLQ selection is invalid or expired."
                ),
                _ => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    detail: "Selected command cannot be discarded."
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
                detail: "Command DLQ discard failed."
            );
        }
    }

    private static async Task<IResult> Redrive(
        CommandDlqSelectionRequest request,
        CommandDlqRedriver redriver,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (request.SelectionTokens is not null)
            {
                if (request.SelectionToken is not null)
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        detail: "Specify one selection or a batch of selections."
                    );
                var batch = await redriver.RedriveBatch(request.SelectionTokens, cancellationToken);

                return batch is null
                    ? Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        detail: "Command DLQ selections are invalid or expired."
                    )
                    : Results.Ok(new { messages = batch });
            }
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

            return inspection.Count == 0 ? Results.NoContent() : Results.Ok(new { messages = inspection });
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
