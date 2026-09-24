namespace Rag.Application;

public interface IAdminAssertionReplayRepository
{
    Task<bool> TryReserveAsync(string issuer, string jti, string appId, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}
