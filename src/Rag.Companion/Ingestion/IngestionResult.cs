namespace Rag.Companion.Ingestion;

/// <summary>Terminal and intermediate states of an ingestion operation as reported by the API.</summary>
public enum OperationState
{
    Pending,
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// Outcome of a single TXT ingestion: document and version identifiers, the operation polled to
/// its terminal state (null for an immediate duplicate), and whether the server reported a duplicate.
/// </summary>
public sealed record IngestionResult(
    Guid DocumentId,
    Guid DocumentVersionId,
    Guid? OperationId,
    bool IsDuplicate);
