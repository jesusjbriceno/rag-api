using Rag.HistoricalLoader.Core.Lifecycle;

namespace Rag.HistoricalLoader.Engine.Pipeline;

/// <summary>
/// Coordinates the durable pause sequence: request a pause (commit <c>desired_state = pause_requested</c>
/// and <c>observed_state = pausing</c>), wait until every document has a durable receipt, then commit
/// <c>observed_state = paused</c>. No unconfirmed work is ever reported paused.
/// </summary>
public sealed class PauseController
{
    private readonly DocumentLifecycleEngine _engine;

    public PauseController(DocumentLifecycleEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public Task<Run> RequestPauseAsync(Guid runId, CancellationToken cancellationToken = default)
        => _engine.PauseAsync(runId, cancellationToken);

    public Task<bool> CanCompletePauseAsync(Guid runId, CancellationToken cancellationToken = default)
        => _engine.CanReportPausedAsync(runId, cancellationToken);

    public Task<Run> CompletePauseAsync(Guid runId, CancellationToken cancellationToken = default)
        => _engine.CompletePauseAsync(runId, cancellationToken);
}
