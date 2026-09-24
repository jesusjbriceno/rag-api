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
            return Results.Ok(new
            {
                id = result.Id,
                status = result.Status.ToString().ToLowerInvariant(),
                failure_stage = result.FailureStage,
                failure_code = result.FailureCode,
                created_at = result.CreatedAt,
                started_at = result.StartedAt,
                completed_at = result.CompletedAt,
                queue_wait = result.QueueWait,
                chunk_count = result.ChunkCount,
                chunking_duration = result.ChunkingDuration,
                embedding_calls = result.EmbeddingCalls,
                embedding_duration = result.EmbeddingDuration,
                indexing_duration = result.IndexingDuration,
                terminal_state = result.TerminalState?.ToString().ToLowerInvariant(),
            });
        }
        catch (Exception exception) when (HistoricalEndpointSupport.TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }
}
