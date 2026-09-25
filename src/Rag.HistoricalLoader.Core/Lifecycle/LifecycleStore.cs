using Rag.HistoricalLoader.Core.Data;

namespace Rag.HistoricalLoader.Core.Lifecycle;

/// <summary>
/// The durable lifecycle persistence contract. Every state change goes through
/// <see cref="SaveAsync"/>, which commits the run_document row and its matching audit event in one
/// SQLite transaction before any UI notification. UI counters are projections from durable rows.
/// </summary>
public interface ILifecycleStore
{
    Task CreateRunAsync(Run run, CancellationToken cancellationToken = default);

    Task<Run?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    Task SetRunDesiredStateAsync(Guid runId, RunDesiredState desiredState, CancellationToken cancellationToken = default);

    Task SetRunObservedStateAsync(Guid runId, RunObservedState observedState, CancellationToken cancellationToken = default);

    Task AddRunDocumentAsync(RunDocument document, CancellationToken cancellationToken = default);

    Task<RunDocument?> GetRunDocumentAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RunDocument>> GetRunDocumentsAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentClaim>> ClaimPendingAsync(Guid runId, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the full <paramref name="document"/> row and appends one audit event atomically. The
    /// audit event records the previous state (read inside the transaction) so the state transition is
    /// never assembled from caller memory.
    /// </summary>
    Task<RunDocument> SaveAsync(
        RunDocument document,
        string action,
        string? outcomeCode,
        int attemptNumber,
        string? measurements,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<DocumentState, int>> CountByStateAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEvent>> GetAuditEventsAsync(Guid runId, CancellationToken cancellationToken = default);
}
