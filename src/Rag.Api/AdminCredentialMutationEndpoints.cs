using Microsoft.AspNetCore.Http;
using Rag.Application;

namespace Rag.Api;

internal static class AdminCredentialMutationEndpoints
{
    public static void MapAdminCredentialMutationEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/credentials/{credentialId:guid}/rotate", RotateCredentialAsync)
            .WithName("rotate_credential")
            .Produces<AdminCredentialDelivery>(StatusCodes.Status200OK);
        group.MapPost("/credentials/{credentialId:guid}/revoke", RevokeCredentialAsync)
            .WithName("revoke_credential")
            .Produces<AdminCredentialMetadata>(StatusCodes.Status200OK);
    }

    private static async Task<IResult> RotateCredentialAsync(
        HttpContext context,
        RotateCredentialHandler handler,
        Guid credentialId,
        CancellationToken cancellationToken)
    {
        if (context.GetAdminActor() is not { } actor)
        {
            return AdminProblem.Create(context, StatusCodes.Status401Unauthorized, "Unauthorized", "unauthorized");
        }

        if (context.GetAdminIdempotencyKey() is not { } idempotencyKey)
        {
            return AdminProblem.Create(context, StatusCodes.Status400BadRequest, "Invalid input", "invalid_input");
        }

        if (!AdminEndpointSupport.TryParseIfMatch(context.Request.Headers["If-Match"].ToString(), out var expectedVersion))
        {
            return AdminProblem.Create(context, StatusCodes.Status400BadRequest, "Invalid input", "invalid_input");
        }

        try
        {
            var result = await handler.HandleAsync(
                actor,
                idempotencyKey,
                context.GetAdminRequestFingerprint() ?? string.Empty,
                credentialId,
                expectedVersion,
                cancellationToken);
            return Results.Ok(result);
        }
        catch (Exception exception)
        {
            return AdminEndpointSupport.MapError(context, exception);
        }
    }

    private static async Task<IResult> RevokeCredentialAsync(
        HttpContext context,
        RevokeCredentialHandler handler,
        Guid credentialId,
        CancellationToken cancellationToken)
    {
        if (context.GetAdminActor() is not { } actor)
        {
            return AdminProblem.Create(context, StatusCodes.Status401Unauthorized, "Unauthorized", "unauthorized");
        }

        if (context.GetAdminIdempotencyKey() is not { } idempotencyKey)
        {
            return AdminProblem.Create(context, StatusCodes.Status400BadRequest, "Invalid input", "invalid_input");
        }

        if (!AdminEndpointSupport.TryParseIfMatch(context.Request.Headers["If-Match"].ToString(), out var expectedVersion))
        {
            return AdminProblem.Create(context, StatusCodes.Status400BadRequest, "Invalid input", "invalid_input");
        }

        try
        {
            return Results.Ok(await handler.HandleAsync(
                actor,
                idempotencyKey,
                context.GetAdminRequestFingerprint() ?? string.Empty,
                credentialId,
                expectedVersion,
                cancellationToken));
        }
        catch (Exception exception)
        {
            return AdminEndpointSupport.MapError(context, exception);
        }
    }
}
