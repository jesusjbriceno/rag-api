using Rag.Companion.Ingestion;

namespace Rag.Companion.Tests;

public sealed class RetryPolicyTests
{
    [Fact]
    public async Task ExecuteAsync_retries_transient_then_returns_value()
    {
        var policy = InstantPolicy();
        var attempts = 0;

        var result = await policy.ExecuteAsync(
            _ =>
            {
                attempts++;
                return attempts < RetryPolicy.MaxAttempts
                    ? Task.FromException<int>(Transient())
                    : Task.FromResult(42);
            },
            static ex => ex is IngestionException { IsTransient: true });

        Assert.Equal(42, result);
        Assert.Equal(RetryPolicy.MaxAttempts, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_never_attempts_a_fourth_time()
    {
        var policy = InstantPolicy();
        var attempts = 0;

        var exception = await Assert.ThrowsAsync<IngestionException>(() => policy.ExecuteAsync(
            _ =>
            {
                attempts++;
                return Task.FromException<int>(Transient());
            },
            static ex => ex is IngestionException { IsTransient: true }));

        Assert.Equal(RetryPolicy.MaxAttempts, attempts);
        Assert.Equal(IngestionErrorCodes.TemporarilyUnavailable, exception.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_does_not_retry_non_transient_failure()
    {
        var policy = InstantPolicy();
        var attempts = 0;

        var exception = await Assert.ThrowsAsync<IngestionException>(() => policy.ExecuteAsync(
            _ =>
            {
                attempts++;
                return Task.FromException<int>(new IngestionException(IngestionErrorCodes.Unauthorized, "rejected"));
            },
            static ex => ex is IngestionException { IsTransient: true }));

        Assert.Equal(1, attempts);
        Assert.Equal(IngestionErrorCodes.Unauthorized, exception.ErrorCode);
    }

    [Theory]
    [InlineData(IngestionErrorCodes.Unauthorized)]
    [InlineData(IngestionErrorCodes.InvalidRequest)]
    [InlineData(IngestionErrorCodes.NotFound)]
    public async Task ExecuteAsync_does_not_retry_http_client_errors(string errorCode)
    {
        var policy = InstantPolicy();
        var attempts = 0;

        var exception = await Assert.ThrowsAsync<IngestionException>(() => policy.ExecuteAsync(
            _ =>
            {
                attempts++;
                return Task.FromException<int>(new IngestionException(errorCode, "rejected"));
            },
            static ex => ex is IngestionException { IsTransient: true }));

        Assert.Equal(1, attempts);
        Assert.Equal(errorCode, exception.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_applies_exponential_backoff_between_attempts()
    {
        var delays = new List<TimeSpan>();
        var policy = new RetryPolicy(
            jitter: () => 0,
            delay: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<IngestionException>(() => policy.ExecuteAsync(
            _ => Task.FromException<int>(Transient()),
            static ex => ex is IngestionException { IsTransient: true }));

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }

    private static RetryPolicy InstantPolicy() => new(
        jitter: () => 0,
        delay: (_, _) => Task.CompletedTask);

    private static IngestionException Transient() =>
        new(IngestionErrorCodes.TemporarilyUnavailable, "temporarily unavailable", isTransient: true);
}
