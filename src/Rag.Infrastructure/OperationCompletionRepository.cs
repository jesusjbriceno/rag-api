using Microsoft.EntityFrameworkCore;
using Rag.Domain;

namespace Rag.Infrastructure;

public interface IOperationCompletionRepository
{
    Task<DocumentVersion?> GetDocumentVersionAsync(Guid documentVersionId, CancellationToken cancellationToken);

    Task<bool> TryCompleteSuccessAsync(Operation operation, IReadOnlyCollection<Chunk> chunks, CancellationToken cancellationToken);

    Task<bool> TryCompleteFailureAsync(Operation operation, string stage, string message, CancellationToken cancellationToken);
}

public sealed class OperationCompletionRepository(IDbContextFactory<IngestionDbContext> dbContextFactory) : IOperationCompletionRepository
{
    public async Task<DocumentVersion?> GetDocumentVersionAsync(Guid documentVersionId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.DocumentVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(version => version.Id == documentVersionId, cancellationToken);
    }

    public async Task<bool> TryCompleteSuccessAsync(
        Operation operation,
        IReadOnlyCollection<Chunk> chunks,
        CancellationToken cancellationToken)
    {
        ValidateClaimedOperation(operation);
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0 || chunks.Any(chunk => chunk.DocumentVersionId != operation.DocumentVersionId))
        {
            throw new ArgumentException("Chunks must belong to the operation document version.", nameof(chunks));
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
