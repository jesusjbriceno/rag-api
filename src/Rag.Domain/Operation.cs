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
        WorkloadClass = OperationWorkloadClass.RealTime;
    }

    public Guid Id { get; private set; }

    public Guid DocumentVersionId { get; private set; }

    public OperationWorkloadClass WorkloadClass { get; private set; }

    public OperationStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public string? LeaseOwner { get; private set; }

    public DateTimeOffset? LeaseExpiresAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? FailureStage { get; private set; }

    public string? FailureMessage { get; private set; }

    public static Operation CreatePending(Guid documentVersionId, DateTimeOffset createdAt) => new(Guid.NewGuid(), documentVersionId, createdAt);

    public static Operation CreatePendingHistorical(Guid documentVersionId, DateTimeOffset createdAt)
    {
        var operation = new Operation(Guid.NewGuid(), documentVersionId, createdAt)
        {
            WorkloadClass = OperationWorkloadClass.Historical,
        };
        return operation;
    }

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

public enum HistoricalUploadState
{
    Reserved,
    Published,
    Committed,
    Abandoned,
}

public sealed class HistoricalUpload
{
    private HistoricalUpload()
    {
    }

    public HistoricalUpload(
        Guid id,
        Guid serviceClientId,
        Guid collectionId,
        string sourceDocumentKey,
        string idempotencyKey,
        string fingerprint,
        string normalizedTextSha256,
        long declaredBytes,
        string correlationId,
        Guid? candidateId,
        Guid? manifestId,
        Guid? runId,
        string? displayName,
        string? format,
        string? sourceRootAlias,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || serviceClientId == Guid.Empty || collectionId == Guid.Empty)
        {
            throw new ArgumentException("Upload, service client, and collection ids are required.");
        }

        if (string.IsNullOrWhiteSpace(sourceDocumentKey) || string.IsNullOrWhiteSpace(idempotencyKey) || string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new ArgumentException("Source document key, idempotency key, and fingerprint are required.");
        }

        if (string.IsNullOrWhiteSpace(normalizedTextSha256) || normalizedTextSha256.Length != 64 || !normalizedTextSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The normalized text hash must be a 64-character hexadecimal SHA-256 digest.", nameof(normalizedTextSha256));
        }

        if (declaredBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(declaredBytes), "Declared bytes must be positive.");
        }

        Id = id;
        ServiceClientId = serviceClientId;
        CollectionId = collectionId;
        SourceDocumentKey = sourceDocumentKey.Trim();
        IdempotencyKey = idempotencyKey.Trim();
        Fingerprint = fingerprint;
        NormalizedTextSha256 = normalizedTextSha256.ToLowerInvariant();
        DeclaredBytes = declaredBytes;
        CorrelationId = correlationId;
        CandidateId = candidateId;
        ManifestId = manifestId;
        RunId = runId;
        DisplayName = NormalizeNullable(displayName);
        Format = NormalizeNullable(format);
        SourceRootAlias = NormalizeNullable(sourceRootAlias);
        CreatedAt = createdAt;
        State = HistoricalUploadState.Reserved;
    }

    public Guid Id { get; private set; }

    public Guid ServiceClientId { get; private set; }

    public Guid CollectionId { get; private set; }

    public string SourceDocumentKey { get; private set; } = null!;

    public string IdempotencyKey { get; private set; } = null!;

    public string Fingerprint { get; private set; } = null!;

    public string NormalizedTextSha256 { get; private set; } = null!;

    public long DeclaredBytes { get; private set; }

    public string CorrelationId { get; private set; } = null!;

    public Guid? CandidateId { get; private set; }

    public Guid? ManifestId { get; private set; }

    public Guid? RunId { get; private set; }

    public string? DisplayName { get; private set; }

    public string? Format { get; private set; }

    public string? SourceRootAlias { get; private set; }

    public HistoricalUploadState State { get; private set; }

    public string? ContentReference { get; private set; }

    public Guid? DocumentId { get; private set; }

    public Guid? DocumentVersionId { get; private set; }

    public Guid? OperationId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public DateTimeOffset? CommittedAt { get; private set; }

    public DateTimeOffset? AbandonedAt { get; private set; }

    public void Publish(string contentReference, DateTimeOffset at)
    {
        EnsureState(HistoricalUploadState.Reserved);
        if (string.IsNullOrWhiteSpace(contentReference))
        {
            throw new ArgumentException("A content reference is required.", nameof(contentReference));
        }

        ContentReference = contentReference;
        PublishedAt = at;
        State = HistoricalUploadState.Published;
    }

    public void Commit(Guid documentId, Guid documentVersionId, Guid operationId, DateTimeOffset at)
    {
        EnsureState(HistoricalUploadState.Published);
        DocumentId = documentId;
        DocumentVersionId = documentVersionId;
        OperationId = operationId;
        CommittedAt = at;
        State = HistoricalUploadState.Committed;
    }

    public void Abandon(DateTimeOffset at)
    {
        if (State is HistoricalUploadState.Committed or HistoricalUploadState.Abandoned)
        {
            return;
        }

        AbandonedAt = at;
        State = HistoricalUploadState.Abandoned;
    }

    private void EnsureState(HistoricalUploadState expected)
    {
        if (State != expected)
        {
            throw new InvalidOperationException($"Upload must be {expected} before this transition.");
        }
    }

    private static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class HistoricalProvenance
{
    private HistoricalProvenance()
    {
    }

    public HistoricalProvenance(
        Guid id,
        Guid documentVersionId,
        string sourceKind,
        string sourceDocumentKey,
        string normalizedTextSha256,
        long declaredBytes,
        Guid remoteOperationId,
        DateTimeOffset ingestedAt,
        Guid? manifestId = null,
        Guid? candidateId = null,
        Guid? runId = null,
        string? sourceRootAlias = null,
        string? displayFileName = null,
        string? format = null)
    {
        if (id == Guid.Empty || documentVersionId == Guid.Empty || remoteOperationId == Guid.Empty)
        {
            throw new ArgumentException("Provenance, document version, and operation ids are required.");
        }

        Id = id;
        DocumentVersionId = documentVersionId;
        SourceKind = sourceKind;
        SourceDocumentKey = sourceDocumentKey;
        NormalizedTextSha256 = normalizedTextSha256.ToLowerInvariant();
        DeclaredBytes = declaredBytes;
        RemoteOperationId = remoteOperationId;
        IngestedAt = ingestedAt;
        ManifestId = manifestId;
        CandidateId = candidateId;
        RunId = runId;
        SourceRootAlias = NormalizeNullable(sourceRootAlias);
        DisplayFileName = NormalizeNullable(displayFileName);
        Format = NormalizeNullable(format);
    }

    public Guid Id { get; private set; }

    public Guid DocumentVersionId { get; private set; }

    public string SourceKind { get; private set; } = null!;

    public string SourceDocumentKey { get; private set; } = null!;

    public Guid? ManifestId { get; private set; }

    public Guid? CandidateId { get; private set; }

    public Guid? RunId { get; private set; }

    public string? SourceRootAlias { get; private set; }

    public string? DisplayFileName { get; private set; }

    public string? Format { get; private set; }

    public long DeclaredBytes { get; private set; }

    public string NormalizedTextSha256 { get; private set; } = null!;

    public DateTimeOffset IngestedAt { get; private set; }

    public Guid RemoteOperationId { get; private set; }

    private static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
    