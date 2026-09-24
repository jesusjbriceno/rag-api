using Microsoft.AspNetCore.Http;
using Rag.Application;

namespace Rag.Api;

internal static class AdminClientEndpoints
{
    public static void MapAdminClientEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/clients", CreateClientAsync)
            .WithName("create_client")
            .DeclaresBody<AdminCreateClientRequest>("application/json", group.IsOpenApiGeneration())
            .Produces<AdminClientMetadata>(StatusCodes.Status201Created)
            .Produces<AdminClientMetadata>(StatusCodes.Status200OK)
            .DeclaresProblem(
                StatusCodes.Status400BadRequest,
                "Bad request. The Idempotency-Key claim is missing, the JSON body is malformed or empty, or the client " +
                "name is invalid (a name of at most 200 characters is required).")
            .DeclaresProblem(
                StatusCodes.Status409Conflict,
                "Conflict. The idempotency key was reused with a different request, a create is already in progress, " +
                "or a client with that name already exists.",
                retryAfterDescription: "Seconds to wait before retrying; when a create is already in progress the " +
                    "response carries Retry-After: 1.")
            .DeclaresProblem(
                StatusCodes.Status415UnsupportedMediaType,
                "Unsupported content type. The handler only accepts application/json.");
        group.MapGet("/clients", ListClientsAsync)
            .WithName("list_clients")
            .Produces<AdminClientPage>(StatusCodes.Status200OK)
            .DeclaresProblem(
                StatusCodes.Status400BadRequest,
                "Bad request. The limit is outside 1..100 or the cursor is invalid.");
        group.MapGet("/clients/{clientId:guid}", GetClientAsync)
            .WithName("get_client")
            .Produces<AdminClientDetail>(StatusCodes.Status200OK)
            .DeclaresProblem(
                StatusCodes.Status404NotFound,
                "Not found. The client does not exist.");
    }

    private static async Task<IResult> CreateClientAsync(
        HttpContext context,
        CreateClientHandler handler,
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

        var payload = await AdminEndpointSupport.ReadJsonAsync<AdminCreateClientRequest>(context, cancellationToken);
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
                payload.Value!.Name,
                payload.Value.Description,
                cancellationToken);
            return result.Replayed
                ? Results.Ok(result.Client)
                : Results.Created($"/api/v1/admin/clients/{result.Client.Id}", result.Client);
        }
        catch (Exception exception)
        {
            return AdminEndpointSupport.MapError(context, exception);
        }
    }

    private static async Task<IResult> ListClientsAsync(
        HttpContext context,
        ListClientsHandler handler,
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

    private static async Task<IResult> GetClientAsync(
        HttpContext context,
        GetClientDetailHandler handler,
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
}

public sealed record AdminCreateClientRequest(string? Name, string? Description);
