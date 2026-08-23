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

    public string? LeaseOwner { get; private set; }

    public DateTimeOffset? LeaseExpiresAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? FailureStage { get; private set; }

    public string? FailureMessage { get; private set; }

    public static Operation CreatePending(Guid documentVersionId, DateTimeOffset createdAt) => new(Guid.NewGuid(), documentVersionId, createdAt);

    public void Claim(string leaseOwner, DateTimeOffset claimedAt, DateTimeOffset leaseExpiresAt)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner))
        {
            throw new ArgumentException("A lease owner is required.", nameof(leaseOwner));
        }

        if (leaseExpiresAt <= claimedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAt), "A lease must expire after it is claimed.");
        }

        if (Status == OperationStatus.Running && LeaseExpiresAt > claimedAt)
        {
            throw new InvalidOperationException("An active operation lease cannot be claimed.");
        }

        if (Status is not (OperationStatus.Pending or OperationStatus.Running))
        {
            throw new InvalidOperationException("Only pending or expired running operations can be claimed.");
        }

        Status = OperationStatus.Running;
        StartedAt ??= claimedAt;
        LeaseOwner = leaseOwner.Trim();
        LeaseExpiresAt = leaseExpiresAt;
    }

    public void Succeed(DateTimeOffset completedAt)
    {
        EnsureStatus(OperationStatus.Running);
        Status = OperationStatus.Succeeded;
        CompletedAt = completedAt;
        ClearLease();
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
        ClearLease();
    }

    private void ClearLease()
    {
        LeaseOwner = null;
        LeaseExpiresAt = null;
    }

    private void EnsureStatus(OperationStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Operation must be {expected} before this transition.");
        }
    }
}
