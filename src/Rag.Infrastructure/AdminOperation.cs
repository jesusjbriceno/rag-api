namespace Rag.Infrastructure;

public sealed class AdminOperation
{
    private AdminOperation()
    {
    }

    public AdminOperation(
        Guid id,
        string appId,
        string idempotencyKey,
        string fingerprint,
        string state,
        DateTimeOffset createdAt,
        string? safeResult = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An operation id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new ArgumentException("An app id is required.", nameof(appId));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        }

        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new ArgumentException("A request fingerprint is required.", nameof(fingerprint));
        }

        if (string.IsNullOrWhiteSpace(state))
        {
            throw new ArgumentException("An operation state is required.", nameof(state));
        }

        Id = id;
        AppId = appId.Trim();
        IdempotencyKey = idempotencyKey.Trim();
        Fingerprint = fingerprint.Trim();
        State = state.Trim();
        CreatedAt = createdAt;
        SafeResult = safeResult;
    }

    public Guid Id { get; private set; }

    public string AppId { get; private set; } = null!;

    public string IdempotencyKey { get; private set; } = null!;

    public string Fingerprint { get; private set; } = null!;

    public string State { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public string? SafeResult { get; private set; }

    public const string ProcessingState = "processing";

    public const string CompletedState = "completed";

    public bool IsCompleted => string.Equals(State, CompletedState, StringComparison.Ordinal);

    public void Complete(string? safeResult)
    {
        State = CompletedState;
        SafeResult = safeResult;
    }
}
