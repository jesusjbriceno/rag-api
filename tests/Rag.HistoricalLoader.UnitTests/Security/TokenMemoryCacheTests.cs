using Rag.HistoricalLoader.Engine.Security;

namespace Rag.HistoricalLoader.UnitTests.Security;

public sealed class TokenMemoryCacheTests
{
    [Fact]
    public async Task Invalidate_after_rotation_reacquires_with_new_credential()
    {
        var current = new RagServiceCredential("old-key", "old-secret");
        var cache = new TokenMemoryCache(_ => Task.FromResult(new IssuedToken($"token-{current.KeyId}", DateTimeOffset.UtcNow.AddMinutes(15))));

        Assert.Equal("token-old-key", await cache.GetAccessTokenAsync());

        current = new RagServiceCredential("new-key", "new-secret");
        cache.Invalidate();

        Assert.Equal("token-new-key", await cache.GetAccessTokenAsync());
    }

    [Fact]
    public async Task Restart_reacquires_token_from_empty_memory()
    {
        var calls = 0;
        IssuedToken Acquire(CancellationToken ct)
        {
            calls++;
            return new IssuedToken($"token-{calls}", DateTimeOffset.UtcNow.AddMinutes(15));
        }

        var first = new TokenMemoryCache((ct) => Task.FromResult(Acquire(ct)));
        Assert.Equal("token-1", await first.GetAccessTokenAsync());
        Assert.Equal("token-1", await first.GetAccessTokenAsync());
        Assert.Equal(1, calls);

        var second = new TokenMemoryCache((ct) => Task.FromResult(Acquire(ct)));
        Assert.Equal("token-2", await second.GetAccessTokenAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Expired_token_reacquires_fresh_token()
    {
        var calls = 0;
        var cache = new TokenMemoryCache(_ => Task.FromResult(new IssuedToken($"token-{++calls}", DateTimeOffset.UtcNow.AddSeconds(1))), safetyMargin: TimeSpan.Zero);

        Assert.Equal("token-1", await cache.GetAccessTokenAsync());
        await Task.Delay(1200);
        Assert.Equal("token-2", await cache.GetAccessTokenAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Concurrent_callers_acquire_once()
    {
        var calls = 0;
        var cache = new TokenMemoryCache(_ =>
        {
            calls++;
            return Task.FromResult(new IssuedToken("token", DateTimeOffset.UtcNow.AddMinutes(15)));
        });

        var tokens = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => cache.GetAccessTokenAsync()));

        Assert.All(tokens, token => Assert.Equal("token", token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Invalidate_forces_reacquisition()
    {
        var calls = 0;
        var cache = new TokenMemoryCache(_ => Task.FromResult(new IssuedToken($"token-{++calls}", DateTimeOffset.UtcNow.AddMinutes(15))));

        Assert.Equal("token-1", await cache.GetAccessTokenAsync());
        cache.Invalidate();
        Assert.Equal("token-2", await cache.GetAccessTokenAsync());
        Assert.Equal(2, calls);
    }
}
