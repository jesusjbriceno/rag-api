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
            .Produces<AdminCredentialDelivery>(StatusCodes.Status201Created);
        group.MapGet("/clients/{clientId:guid}/credentials", ListCredentialsAsync)
            .WithName("list_credentials")
            .Produces<IReadOnlyList<AdminCredentialMetadata>>(StatusCodes.Status200OK);
        group.MapGet("/credentials/{credentialId:guid}", GetCredentialAsync)
            .WithName("get_credential")
            .Produces<AdminCredentialMetadata>(StatusCodes.Status200OK);
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
