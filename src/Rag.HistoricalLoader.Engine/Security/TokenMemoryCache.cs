namespace Rag.HistoricalLoader.Engine.Security;

/// <summary>Short-lived bearer token kept only in process memory.</summary>
public sealed record IssuedToken(string Value, DateTimeOffset ExpiresAt, string? Scope = null);

/// <summary>
/// Memory-only short-lived token cache. Tokens are never persisted or checkpointed, so a new process
/// (restart) starts empty and reacquires. A single-flight gate prevents concurrent reacquisition.
/// </summary>
public sealed class TokenMemoryCache
{
    private readonly Func<CancellationToken, Task<IssuedToken>> _acquirer;
    private readonly TimeSpan _safetyMargin;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IssuedToken? _token;

    public TokenMemoryCache(Func<CancellationToken, Task<IssuedToken>> acquirer, TimeSpan? safetyMargin = null)
    {
        _acquirer = acquirer ?? throw new ArgumentNullException(nameof(acquirer));
        _safetyMargin = safetyMargin ?? TimeSpan.FromSeconds(30);
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsUsable(_token))
        {
            return _token!.Value;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsUsable(_token))
            {
                return _token!.Value;
            }

            var token = await _acquirer(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token.Value))
            {
                throw new InvalidOperationException("The token acquirer returned an empty token.");
            }

            _token = token;
            return token.Value;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _token = null;

    private bool IsUsable(IssuedToken? token)
        => token is not null && token.ExpiresAt > DateTimeOffset.UtcNow + _safetyMargin;
}
