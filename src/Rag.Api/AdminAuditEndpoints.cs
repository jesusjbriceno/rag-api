using Microsoft.AspNetCore.Http;
using Rag.Application;

namespace Rag.Api;

internal static class AdminAuditEndpoints
{
    public static void MapAdminAuditEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/audit", ListAuditAsync);
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
