using Microsoft.AspNetCore.Http;
using Rag.Application;

namespace Rag.Api;

internal static class AdminCredentialMutationEndpoints
{
    public static void MapAdminCredentialMutationEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/credentials/{credentialId:guid}/rotate", RotateCredentialAsync)
            .WithName("rotate_credential")
            .Produces<AdminCredentialDelivery>(StatusCodes.Status200OK)
            .DeclaresHeader(
                "If-Match",
                required: true,
                "The quoted current credential version to rotate, for example \"v3\". A missing or malformed value is " +
                "rejected with 400.")
            .DeclaresProblem(
                StatusCodes.Status400BadRequest,
                "Bad request. The Idempotency-Key claim is missing, the If-Match header is missing or malformed (the " +
                "quoted version form \"v3\" is required), or the expected version is invalid.")
            .DeclaresProblem(
                StatusCodes.Status404NotFound,
                "Not found. The credential does not exist.")
            .DeclaresProblem(
                StatusCodes.Status409Conflict,
                "Conflict. The If-Match version does not match the current version, the credential is not active, the " +
                "idempotency key was reused, or a rotation is already in progress.");
        group.MapPost("/credentials/{credentialId:guid}/revoke", RevokeCredentialAsync)
            .WithName("revoke_credential")
            .Produces<AdminCredentialMetadata>(StatusCodes.Status200OK)
            .DeclaresHeader(
                "If-Match",
                required: true,
                "The quoted current credential version to revoke, for example \"v3\". A missing or malformed value is " +
                "rejected with 400.")
            .DeclaresProblem(
                StatusCodes.Status400BadRequest,
                "Bad request. The Idempotency-Key claim is missing, the If-Match header is missing or malformed (the " +
                "quoted version form \"v3\" is required), or the expected version is invalid.")
            .DeclaresProblem(
                StatusCodes.Status404NotFound,
                "Not found. The credential does not exist.")
            .DeclaresProblem(
                StatusCodes.Status409Conflict,
                "Conflict. The If-Match version does not match the current version, the idempotency key was reused, " +
                "or a revoke is already in progress.");
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
