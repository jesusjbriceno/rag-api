namespace Rag.Api.Historical;

public static class HistoricalOperationEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/v1/historical/collections/{collectionId:guid}/operations/{operationId:guid}",
            GetAsync)
            .RequireAuthorization(HistoricalAuthorizationPolicies.OperationsRead);
    }

    private static async Task<IResult> GetAsync(
        Guid collectionId,
        Guid operationId,
        HttpContext context,
        HistoricalOperationHandler handler,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await handler.GetAsync(
                ApiEndpointSupport.GetClientId(context.User),
                collectionId,
                operationId,
                cancellationToken);
            return Results.Ok(new HistoricalOperationTelemetryResponse(
                result.Id,
                result.Status.ToString().ToLowerInvariant(),
                result.FailureStage,
                result.FailureCode,
                result.CreatedAt,
                result.StartedAt,
                result.CompletedAt,
                result.QueueWait,
                result.ChunkCount,
                result.ChunkingDuration,
                result.EmbeddingCalls,
                result.EmbeddingDuration,
                result.IndexingDuration,
                result.TerminalState?.ToString().ToLowerInvariant()));
        }
        catch (Exception exception) when (HistoricalEndpointSupport.TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }
}
