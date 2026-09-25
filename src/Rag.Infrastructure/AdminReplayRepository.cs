using Microsoft.EntityFrameworkCore;
using Npgsql;
using Rag.Application;

namespace Rag.Infrastructure;

public sealed class AdminReplayRepository(IDbContextFactory<IngestionDbContext> dbContextFactory) : IAdminAssertionReplayRepository
{
    public async Task<bool> ReserveAsync(
        string issuer,
        string jti,
        string appId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.AdminAssertionReplays.Add(new AdminAssertionReplay(
            Guid.NewGuid(),
            issuer,
            jti,
            appId,
            expiresAt,
            DateTimeOffset.UtcNow));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return false;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
