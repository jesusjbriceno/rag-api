using Rag.HistoricalLoader.Engine.Pipeline;

namespace Rag.HistoricalLoader.UnitTests.Pipeline;

public sealed class RetryBackoffTests
{
    [Fact]
    public void FullJitter_HonorsRetryAfter()
    {
        var delay = RetryBackoff.FullJitter(
            TimeSpan.FromSeconds(1),
            dispatchedAttempts: 1,
            TimeSpan.FromSeconds(60),
            retryAfter: TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(5), delay);
    }

    [Fact]
    public void FullJitter_BoundsRetryAfter()
    {
        var delay = RetryBackoff.FullJitter(
            TimeSpan.FromSeconds(1),
            dispatchedAttempts: 1,
            TimeSpan.FromSeconds(10),
            retryAfter: TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(10), delay);
    }

    [Fact]
    public void FullJitter_StaysWithinExponentialCap()
    {
        var maxDelay = TimeSpan.FromSeconds(60);
        var delay = RetryBackoff.FullJitter(
            TimeSpan.FromSeconds(1),
            dispatchedAttempts: 3,
            maxDelay);

        Assert.InRange(delay, TimeSpan.Zero, TimeSpan.FromSeconds(4)); // baseDelay * 2^(3-1) = 4s
        Assert.True(delay <= maxDelay);
    }

    [Fact]
    public void FullJitter_IsDeterministicPerSeed()
    {
        var baseDelay = TimeSpan.FromSeconds(1);

        var first = RetryBackoff.FullJitter(baseDelay, 2, TimeSpan.FromSeconds(60), random: new Random(42));
        var second = RetryBackoff.FullJitter(baseDelay, 2, TimeSpan.FromSeconds(60), random: new Random(42));

        Assert.Equal(first, second);
    }
}
