using Microsoft.AspNetCore.Http;
using Rag.Application;

namespace Rag.Api;

internal static class AdminAuditEndpoints
{
    public static void MapAdminAuditEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/audit", ListAuditAsync)
            .WithName("list_audit")
            .Produces<AdminAuditPage>(StatusCodes.Status200OK)
            .DeclaresProblem(
                StatusCodes.Status400BadRequest,
                "Bad request. The limit is outside 1..100 or the cursor is invalid.");
    }

    private static async Task<IResult> ListAuditAsync(
        HttpContext context,
        ListAuditHandler handler,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await handler.HandleAsync(limit, cursor, cancellationToken));
        }
        catch (Exception exception)
        {
            return AdminEndpointSupport.MapError(context, exception);
        }
    }
}
