namespace Rag.Companion.Ingestion;

/// <summary>
/// Bounded transient-retry executor: at most three attempts with exponential backoff of roughly
/// 1s, then 2s (a 4s step documents the schedule but is never reached with three attempts), each
/// jittered upward. Only failures the caller classifies as transient are retried; a non-transient
/// failure propagates immediately, and cancellation is never retried.
/// </summary>
public sealed class RetryPolicy
{
    public const int MaxAttempts = 3;

    private static readonly TimeSpan[] BackoffSchedule =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    private readonly Func<double> _jitter;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public RetryPolicy(
        Func<double>? jitter = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _jitter = jitter ?? (static () => Random.Shared.NextDouble());
        _delay = delay ?? (static (delay, cancellationToken) => Task.Delay(delay, cancellationToken));
    }

    /// <summary>
    /// Executes <paramref name="operation"/> up to <see cref="MaxAttempts"/> times. A failure that
    /// <paramref name="isTransient"/> classifies as transient is retried after an exponential
    /// backoff delay; anything else escapes immediately.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<Exception, bool> isTransient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(isTransient);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < MaxAttempts - 1 && isTransient(exception))
            {
                await DelayAsync(BackoffSchedule[attempt], cancellationToken).ConfigureAwait(false);
            }
        }

        // Unreachable: the final attempt's exception escapes the catch filter and propagates.
        throw new InvalidOperationException("Retry loop exited without a result or exception.");
    }

    private async Task DelayAsync(TimeSpan baseDelay, CancellationToken cancellationToken)
    {
        var jittered = baseDelay + baseDelay * _jitter();
        await _delay(jittered, cancellationToken).ConfigureAwait(false);
    }
}
