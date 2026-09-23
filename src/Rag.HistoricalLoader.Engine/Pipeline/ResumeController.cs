using Rag.HistoricalLoader.Core.Lifecycle;

namespace Rag.HistoricalLoader.Engine.Pipeline;

/// <summary>
/// Recovers transient work after a forced termination and restarts a paused run. Reconciliation examines
/// durable receipts and returns local-only transient work to its preceding safe stage; resume never
/// resets attempt counts and never re-marks terminal outcomes as pending.
/// </summary>
public sealed class ResumeController
{
    private readonly DocumentLifecycleEngine _engine;
    private readonly ILifecycleStore _store;

    public ResumeController(DocumentLifecycleEngine engine, ILifecycleStore store)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<IReadOnlyList<RunDocument>> ReconcileAsync(Guid runId, CancellationToken cancellationToken = default)
        => _engine.ReconcileAsync(runId, cancellationToken);

    public async Task<Run?> ResumeAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await _store.SetRunDesiredStateAsync(runId, RunDesiredState.Running, cancellationToken);
        await _store.SetRunObservedStateAsync(runId, RunObservedState.Running, cancellationToken);
        return await _store.GetRunAsync(runId, cancellationToken);
    }
}
