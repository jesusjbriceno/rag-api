using Microsoft.EntityFrameworkCore;
using Rag.Application;
using Rag.Domain;

namespace Rag.Infrastructure;

public interface IOperationCompletionRepository
{
    Task<OperationIndexingTarget?> GetIndexingTargetAsync(Guid documentVersionId, CancellationToken cancellationToken);

    Task<bool> TryCompleteSuccessAsync(
        Operation operation,
        OperationIndexingTarget target,
        IReadOnlyCollection<Chunk> chunks,
        IReadOnlyCollection<ChunkEmbeddingInput> embeddings,
        CancellationToken cancellationToken);

    Task<bool> TryCompleteFailureAsync(Operation operation, string stage, string message, CancellationToken cancellationToken);
}

public sealed record OperationIndexingTarget(DocumentVersion Version, Guid CollectionId, EmbeddingProfile Profile);

public sealed record ChunkEmbeddingInput(Guid ChunkId, float[] Values);

public sealed class OperationCompletionRepository(IDbContextFactory<IngestionDbContext> dbContextFactory) : IOperationCompletionRepository
{
    public async Task<OperationIndexingTarget?> GetIndexingTargetAsync(Guid documentVersionId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var version = await dbContext.DocumentVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(version => version.Id == documentVersionId, cancellationToken);
        if (version is null)
        {
            return null;
        }

        var collection = await (
            from document in dbContext.Documents.AsNoTracking()
            join item in dbContext.Collections.AsNoTracking() on document.CollectionId equals item.Id
            where document.Id == version.DocumentId
            select new
            {
                document.CollectionId,
                item.EmbeddingProvider,
                item.EmbeddingModel,
                item.EmbeddingVersion,
                item.EmbeddingDimensions,
            }).SingleOrDefaultAsync(cancellationToken);
        return collection is null
            ? null
            : new OperationIndexingTarget(
                version,
                collection.CollectionId,
                new EmbeddingProfile(
                    collection.EmbeddingProvider,
                    collection.EmbeddingModel,
                    collection.EmbeddingVersion,
                    collection.EmbeddingDimensions));
    }

    public async Task<bool> TryCompleteSuccessAsync(
        Operation operation,
        OperationIndexingTarget target,
        IReadOnlyCollection<Chunk> chunks,
        IReadOnlyCollection<ChunkEmbeddingInput> embeddings,
        CancellationToken cancellationToken)
    {
        ValidateClaimedOperation(operation);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(embeddings);
        if (chunks.Count == 0 || chunks.Any(chunk => chunk.DocumentVersionId != operation.DocumentVersionId)
            || target.Version.Id != operation.DocumentVersionId
            || embeddings.Count != chunks.Count
            || embeddings.Any(embedding => embedding.Values.Length != target.Profile.Dimensions || embedding.Values.Any(value => !float.IsFinite(value)) || chunks.All(chunk => chunk.Id != embedding.ChunkId)))
        {
            throw new ArgumentException("Chunks and embeddings must match the operation document version and profile.");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var updated = await CompleteAsync(dbContext, operation, "Succeeded", cancellationToken);
        if (updated == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        dbContext.Chunks.AddRange(chunks);
        dbContext.ChunkEmbeddings.AddRange(embeddings.Select(embedding => new ChunkEmbedding(
            Guid.NewGuid(),
            target.CollectionId,
            embedding.ChunkId,
            embedding.Values)));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> TryCompleteFailureAsync(
        Operation operation,
        string stage,
        string message,
        CancellationToken cancellationToken)
    {
        ValidateClaimedOperation(operation);
        if (string.IsNullOrWhiteSpace(stage) || string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A failure stage and message are required.");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var updated = await CompleteAsync(dbContext, operation, "Failed", cancellationToken, stage.Trim(), message.Trim());
        if (updated == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static Task<int> CompleteAsync(
        IngestionDbContext dbContext,
        Operation operation,
        string status,
        CancellationToken cancellationToken,
        string? stage = null,
        string? message = null) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE operations
            SET "Status" = {status},
                "CompletedAt" = clock_timestamp(),
                "FailureStage" = {stage},
                "FailureMessage" = {message},
                "LeaseOwner" = NULL,
                "LeaseExpiresAt" = NULL
            WHERE "Id" = {operation.Id}
              AND "Status" = 'Running'
              AND "LeaseOwner" = {operation.LeaseOwner!}
              AND "LeaseExpiresAt" = {operation.LeaseExpiresAt!.Value}
              AND "LeaseExpiresAt" > clock_timestamp()
            """, cancellationToken);

    private static void ValidateClaimedOperation(Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Status != OperationStatus.Running || string.IsNullOrWhiteSpace(operation.LeaseOwner) || operation.LeaseExpiresAt is null)
        {
            throw new ArgumentException("A running operation with an active lease is required.", nameof(operation));
        }
    }
}
