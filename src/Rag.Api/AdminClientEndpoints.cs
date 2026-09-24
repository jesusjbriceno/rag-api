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
            .Produces<AdminClientMetadata>(StatusCodes.Status200OK);
        group.MapGet("/clients", ListClientsAsync)
            .WithName("list_clients")
            .Produces<AdminClientPage>(StatusCodes.Status200OK);
        group.MapGet("/clients/{clientId:guid}", GetClientAsync)
            .WithName("get_client")
            .Produces<AdminClientDetail>(StatusCodes.Status200OK);
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
