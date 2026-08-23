namespace Rag.Domain;

public enum OperationStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
}

public sealed class Operation
{
    private Operation()
    {
    }

    private Operation(Guid id, Guid documentVersionId, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || documentVersionId == Guid.Empty)
        {
            throw new ArgumentException("Operation and document version ids are required.");
        }

        Id = id;
        DocumentVersionId = documentVersionId;
        CreatedAt = createdAt;
        Status = OperationStatus.Pending;
    }

    public Guid Id { get; private set; }

    public Guid DocumentVersionId { get; private set; }

    public OperationStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? FailureStage { get; private set; }

    public string? FailureMessage { get; private set; }

    public static Operation CreatePending(Guid documentVersionId, DateTimeOffset createdAt) => new(Guid.NewGuid(), documentVersionId, createdAt);

    public void Start(DateTimeOffset startedAt)
    {
        EnsureStatus(OperationStatus.Pending);
        Status = OperationStatus.Running;
        StartedAt = startedAt;
    }

    public void Succeed(DateTimeOffset completedAt)
    {
        EnsureStatus(OperationStatus.Running);
        Status = OperationStatus.Succeeded;
        CompletedAt = completedAt;
    }

    public void Fail(string stage, string message, DateTimeOffset completedAt)
    {
        EnsureStatus(OperationStatus.Running);
        if (string.IsNullOrWhiteSpace(stage) || string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A failure stage and message are required.");
        }

        Status = OperationStatus.Failed;
        FailureStage = stage.Trim();
        FailureMessage = message.Trim();
        CompletedAt = completedAt;
    }

    private void EnsureStatus(OperationStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Operation must be {expected} before this transition.");
        }
    }
}
