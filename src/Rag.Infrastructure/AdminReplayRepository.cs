using Microsoft.EntityFrameworkCore;
using Npgsql;
using Rag.Application;

namespace Rag.Infrastructure;

public sealed class AdminReplayRepository(IngestionDbContext context) : IAdminAssertionReplayRepository
{
    public async Task<bool> TryReserveAsync(string issuer, string jti, string appId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        context.AdminAssertionReplays.Add(new AdminAssertionReplay(
            Guid.NewGuid(),
            issuer,
            jti,
            appId,
            expiresAt,
            DateTimeOffset.UtcNow));

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            context.ChangeTracker.Clear();
            return false;
        }
    }
}
