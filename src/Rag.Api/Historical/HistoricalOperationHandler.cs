using Microsoft.EntityFrameworkCore;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.Api.Historical;

public sealed record HistoricalOperationStatusResult(
    Guid Id,
    OperationStatus Status,
    string? FailureStage,
    string? FailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    TimeSpan QueueWait,
    int ChunkCount,
    TimeSpan ChunkingDuration,
    int EmbeddingCalls,
    TimeSpan EmbeddingDuration,
    TimeSpan IndexingDuration,
    OperationTerminalState? TerminalState);

public sealed class HistoricalOperationHandler(IngestionDbContext dbContext, HistoricalTelemetry telemetry)
{
    public async Task<HistoricalOperationStatusResult> GetAsync(
        Guid serviceClientId,
        Guid collectionId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var operation = await (
            from item in dbContext.Operations.AsNoTracking()
            join version in dbContext.DocumentVersions.AsNoTracking() on item.DocumentVersionId equals version.Id
            join document in dbContext.Documents.AsNoTracking() on version.DocumentId equals document.Id
            where item.Id == operationId
                && document.CollectionId == collectionId
                && item.WorkloadClass == OperationWorkloadClass.Historical
            join collection in dbContext.Collections.AsNoTracking() on document.CollectionId equals collection.Id
            where collection.ServiceClientId == serviceClientId
            select new
            {
                item.Id,
                item.Status,
                item.FailureStage,
                item.CreatedAt,
                item.StartedAt,
                item.CompletedAt,
            }).SingleOrDefaultAsync(cancellationToken);

        if (operation is null)
        {
            throw new HistoricalUploadNotFoundException();
        }

        var sample = telemetry.Snapshot().Samples.SingleOrDefault(sample => sample.OperationId == operationId);
        var failureCode = SafeFailureStage(operation.FailureStage);
        return new HistoricalOperationStatusResult(
            operation.Id,
            operation.Status,
            failureCode,
            failureCode,
            operation.CreatedAt,
            operation.StartedAt,
            operation.CompletedAt,
            sample?.QueueWait ?? TimeSpan.Zero,
            sample?.ChunkCount ?? 0,
            sample?.ChunkingDuration ?? TimeSpan.Zero,
            sample?.EmbeddingRequestCount ?? 0,
            sample?.EmbeddingDuration ?? TimeSpan.Zero,
            sample?.IndexingDuration ?? TimeSpan.Zero,
            sample?.TerminalState);
    }

    private static string? SafeFailureStage(string? stage) => stage is "load" or "parse" or "embed" or "index" ? stage : null;
}
