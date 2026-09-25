namespace Rag.Application;

public interface IAdminAssertionReplayRepository
{
    Task<bool> ReserveAsync(
        string issuer,
        string jti,
        string appId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);
}
