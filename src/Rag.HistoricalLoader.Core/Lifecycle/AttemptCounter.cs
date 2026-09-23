namespace Rag.HistoricalLoader.Core.Lifecycle;

/// <summary>
/// Immutable per-operation attempt counter. A dispatched HTTP operation has at most
/// <see cref="MaxAttempts"/> attempts per run/generation. A fourth dispatch is refused; an attempt is
/// recorded (persisted) before dispatch so a crash after dispatch never re-runs a mutation past the
/// ceiling. Cancellation requested before dispatch consumes nothing.
/// </summary>
public sealed record AttemptCounter
{
    public const int MaxAttempts = 3;

    public int DispatchedAttempts { get; init; }

    public bool CanDispatch => DispatchedAttempts < MaxAttempts;

    public bool IsExhausted => DispatchedAttempts >= MaxAttempts;

    public static AttemptCounter Fresh { get; } = new();

    public AttemptCounter RecordDispatch()
    {
        if (IsExhausted)
        {
            throw new InvalidOperationException(
                $"Cannot dispatch a fourth attempt; the {MaxAttempts}-attempt ceiling is already reached.");
        }

        return this with { DispatchedAttempts = DispatchedAttempts + 1 };
    }

    public static AttemptCounter From(int dispatchedAttempts)
    {
        if (dispatchedAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dispatchedAttempts), "Attempt count cannot be negative.");
        }

        return new AttemptCounter { DispatchedAttempts = dispatchedAttempts };
    }
}
