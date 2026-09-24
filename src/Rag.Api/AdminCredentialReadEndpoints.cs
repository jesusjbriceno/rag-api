using Microsoft.AspNetCore.Http;
using Rag.Application;

namespace Rag.Api;

internal static class AdminCredentialReadEndpoints
{
    public static void MapAdminCredentialReadEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/clients/{clientId:guid}/credentials", IssueCredentialAsync)
            .WithName("issue_credential")
            .DeclaresBody<AdminIssueCredentialRequest>("application/json", group.IsOpenApiGeneration())
            .Produces<AdminCredentialDelivery>(StatusCodes.Status201Created)
            .DeclaresProblem(
                StatusCodes.Status400BadRequest,
                "Bad request. The Idempotency-Key claim is missing, the JSON body is malformed or empty, the client id " +
                "is empty, or the credential expiry is not in the future.")
            .DeclaresProblem(
                StatusCodes.Status404NotFound,
                "Not found. The client does not exist.")
            .DeclaresProblem(
                StatusCodes.Status409Conflict,
                "Conflict. The idempotency key was reused, a secret was already delivered for this request, or an " +
                "issue is already in progress.")
            .DeclaresProblem(
                StatusCodes.Status415UnsupportedMediaType,
                "Unsupported content type. The handler only accepts application/json.");
        group.MapGet("/clients/{clientId:guid}/credentials", ListCredentialsAsync)
            .WithName("list_credentials")
            .Produces<IReadOnlyList<AdminCredentialMetadata>>(StatusCodes.Status200OK)
            .DeclaresProblem(
                StatusCodes.Status404NotFound,
                "Not found. The client does not exist.");
        group.MapGet("/credentials/{credentialId:guid}", GetCredentialAsync)
            .WithName("get_credential")
            .Produces<AdminCredentialMetadata>(StatusCodes.Status200OK)
            .DeclaresProblem(
                StatusCodes.Status404NotFound,
                "Not found. The credential does not exist.");
    }

    private static async Task<IResult> IssueCredentialAsync(
        HttpContext context,
        IssueCredentialHandler handler,
        Guid clientId,
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

        var payload = await AdminEndpointSupport.ReadJsonAsync<AdminIssueCredentialRequest>(context, cancellationToken);
        if (payload.Error is not null)
        {
            return payload.Error;
        }

        try
        {
            var result = await handler.HandleAsync(
                actor,
                idempotencyKey,
                context.GetAdminRequestFingerprint() ?? string.Empty,
                clientId,
                payload.Value!.Description,
                payload.Value.ExpiresAt,
                cancellationToken);
            return Results.Created($"/api/v1/admin/credentials/{result.Credential.Id}", result);
        }
        catch (Exception exception)
        {
            return AdminEndpointSupport.MapError(context, exception);
        }
    }

    private static async Task<IResult> ListCredentialsAsync(
        HttpContext context,
        ListCredentialsHandler handler,
        Guid clientId,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await handler.HandleAsync(clientId, cancellationToken));
        }
        catch (Exception exception)
        {
            return AdminEndpointSupport.MapError(context, exception);
        }
    }

    private static async Task<IResult> GetCredentialAsync(
        HttpContext context,
        GetCredentialHandler handler,
        Guid credentialId,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await handler.HandleAsync(credentialId, cancellationToken));
        }
        catch (Exception exception)
        {
            return AdminEndpointSupport.MapError(context, exception);
        }
    }
}

public sealed record AdminIssueCredentialRequest(string? Description, DateTimeOffset? ExpiresAt);
