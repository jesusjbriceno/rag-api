using Microsoft.EntityFrameworkCore;
using Rag.Domain;

namespace Rag.Infrastructure;

public interface IOperationClaimRepository
{
    Task<Operation?> ClaimNextAsync(
        string leaseOwner,
        DateTimeOffset claimedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);
}

public sealed class OperationClaimRepository(IDbContextFactory<IngestionDbContext> dbContextFactory) : IOperationClaimRepository
{
    public async Task<Operation?> ClaimNextAsync(
        string leaseOwner,
        DateTimeOffset claimedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner))
        {
            throw new ArgumentException("A lease owner is required.", nameof(leaseOwner));
        }

        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "A lease duration must be positive.");
        }

        var normalizedLeaseOwner = leaseOwner.Trim();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var operation = await dbContext.Operations.FromSqlRaw("""
            SELECT *
            FROM operations
            WHERE "Status" = 'Pending'
               OR ("Status" = 'Running' AND "LeaseExpiresAt" <= clock_timestamp())
            ORDER BY "CreatedAt", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """).SingleOrDefaultAsync(cancellationToken);

        if (operation is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE operations
            SET "Status" = 'Running',
                "StartedAt" = COALESCE("StartedAt", clock_timestamp()),
                "LeaseOwner" = {normalizedLeaseOwner},
                "LeaseExpiresAt" = clock_timestamp() + {leaseDuration}
            WHERE "Id" = {operation.Id}
            """, cancellationToken);
        await dbContext.Entry(operation).ReloadAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return operation;
    }
}
