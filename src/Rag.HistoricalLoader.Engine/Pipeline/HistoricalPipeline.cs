using System.Text;
using Rag.HistoricalLoader.Core.Extraction;
using Rag.HistoricalLoader.Core.Lifecycle;

namespace Rag.HistoricalLoader.Engine.Pipeline;

/// <summary>
/// Bounded, single-worker pipeline that wires the selected extractor, the real API client (Unit 9), and
/// the SQLite lifecycle (Unit 6) together. It claims one durable pending document at a time (concurrency
/// one), tracks staged byte/count watermarks, drains to a durable boundary on pause, and classifies
/// terminal outcomes. Loaded is reported only from a durable <see cref="DocumentState.Loaded"/> receipt.
/// </summary>
public sealed class HistoricalPipeline
{
    private readonly SqliteRunStore _runStore;
    private readonly PipelineOptions _options;
    private readonly WatermarkScheduler _watermarks;
    private readonly DocumentLifecycleEngine _engine;
    private readonly PauseController _pause;
    private readonly ResumeController _resume;

    public HistoricalPipeline(
        IExtractor extractor,
        IHistoricalApiClient api,
        SqliteRunStore runStore,
        PipelineOptions options,
        WatermarkScheduler? watermarks = null)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(api);
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (options.Concurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Concurrency must be at least one.");
        }

        if (options.StagedByteWatermark < 0 || options.StagedCountWatermark < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Watermarks cannot be negative.");
        }

        _watermarks = watermarks ?? new WatermarkScheduler(options.StagedByteWatermark, options.StagedCountWatermark);

        var engine = new DocumentLifecycleEngine(new WatermarkTrackingExtractor(extractor, _watermarks), api, runStore);
        _engine = engine;
        _pause = new PauseController(engine);
        _resume = new ResumeController(engine, _runStore);
    }

    public WatermarkScheduler Watermarks => _watermarks;

    public Task<Run> RequestPauseAsync(Guid runId, CancellationToken cancellationToken = default)
        => _pause.RequestPauseAsync(runId, cancellationToken);

    public Task<Run?> ResumeAsync(Guid runId, CancellationToken cancellationToken = default)
        => _resume.ResumeAsync(runId, cancellationToken);

    public async Task<PipelineRunResult> RunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await _resume.ReconcileAsync(runId, cancellationToken);

        // Durable watermark rehydration: a restart builds a fresh scheduler, so the staged capacity and the
        // committed watermark are recovered from the run's durable rows before anything is claimed. Nothing
        // is released here — only a server-confirmed loaded row advances the committed watermark.
        _watermarks.Rehydrate(await _runStore.GetWatermarkRowsAsync(runId, cancellationToken));

        var claimed = 0;
        var loaded = 0;
        var skipped = 0;
        var retryExhausted = 0;
        var blockedAuth = 0;
        var blockedOperator = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var run = await _runStore.GetRunAsync(runId, cancellationToken)
                ?? throw new InvalidOperationException($"Run '{runId}' was not found.");

            if (run.ObservedState == RunObservedState.BlockedAuth)
            {
                return Build(claimed, loaded, skipped, retryExhausted, blockedAuth, blockedOperator,
                    paused: false, completed: false, blockReason: FailureClass.Authentication);
            }

            if (run.ObservedState == RunObservedState.BlockedOperatorAction)
            {
                return Build(claimed, loaded, skipped, retryExhausted, blockedAuth, blockedOperator,
                    paused: false, completed: false, blockReason: FailureClass.ContractData);
            }

            if (run.DesiredState == RunDesiredState.PauseRequested
                && await _pause.CanCompletePauseAsync(runId, cancellationToken))
            {
                await _pause.CompletePauseAsync(runId, cancellationToken);
                return Build(claimed, loaded, skipped, retryExhausted, blockedAuth, blockedOperator,
                    paused: true, completed: false, blockReason: null);
            }

            if (_watermarks.IsStageWatermarkReached)
            {
                return Build(claimed, loaded, skipped, retryExhausted, blockedAuth, blockedOperator,
                    paused: false, completed: false, blockReason: null);
            }

            var claim = await _engine.TryClaimNextAsync(runId, cancellationToken);
            if (claim is null)
            {
                if (run.DesiredState == RunDesiredState.PauseRequested
                    && await _pause.CanCompletePauseAsync(runId, cancellationToken))
                {
                    await _pause.CompletePauseAsync(runId, cancellationToken);
                    return Build(claimed, loaded, skipped, retryExhausted, blockedAuth, blockedOperator,
                        paused: true, completed: false, blockReason: null);
                }

                await _runStore.SetRunObservedStateAsync(runId, RunObservedState.Completed, cancellationToken);
                return Build(claimed, loaded, skipped, retryExhausted, blockedAuth, blockedOperator,
                    paused: false, completed: true, blockReason: null);
            }

            claimed++;

            var document = await _runStore.GetRunDocumentAsync(claim.RunDocumentId, cancellationToken)
                ?? throw new InvalidOperationException($"Run document '{claim.RunDocumentId}' was not found.");

            RunDocument result;
            try
            {
                result = await _engine.ProcessDocumentAsync(document, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                await _runStore.SetRunObservedStateAsync(runId, RunObservedState.BlockedOperatorAction, cancellationToken);
                return Build(claimed, loaded, skipped, retryExhausted, blockedAuth, blockedOperator + 1,
                    paused: false, completed: false, blockReason: FailureClassifier.ClassifyException(exception));
            }

                // Persist the staged byte measurement before any release so a later restart can rehydrate both
                // the staged capacity and the committed watermark of this document from durable rows alone.
                if (_watermarks.TryGetStagedBytes(result.SourceDocumentKey, out var stagedBytes))
                {
                    await _runStore.RecordStagedBytesAsync(runId, result.SourceDocumentKey, stagedBytes, cancellationToken);
                }

                switch (result.State)
            {
                case DocumentState.Loaded:
                    _watermarks.ReleaseCommitted(result.SourceDocumentKey);
                    loaded++;
                    break;

                case DocumentState.SkippedDocumentError:
                    skipped++;
                    break;

                case DocumentState.RetryExhaustedNetwork:
                    retryExhausted++;
                    break;

                case DocumentState.BlockedAuth:
                    blockedAuth++;
                    await _runStore.SetRunObservedStateAsync(runId, RunObservedState.BlockedAuth, cancellationToken);
                    return Build(claimed, loaded, skipped, retryExhausted, blockedAuth, blockedOperator,
                        paused: false, completed: false, blockReason: FailureClass.Authentication);

                case DocumentState.BlockedOperatorAction:
                    blockedOperator++;
                    await _runStore.SetRunObservedStateAsync(runId, RunObservedState.BlockedOperatorAction, cancellationToken);
                    return Build(claimed, loaded, skipped, retryExhausted, blockedAuth, blockedOperator,
                        paused: false, completed: false, blockReason: FailureClass.ContractData);

                default:
                    // RemotePending (poll still pending) and other non-terminal states are not reported loaded.
                    break;
            }
        }
    }

    private PipelineRunResult Build(
        int claimed, int loaded, int skipped, int retryExhausted, int blockedAuth, int blockedOperator,
        bool paused, bool completed, FailureClass? blockReason)
        => new(
            claimed,
            loaded,
            skipped,
            retryExhausted,
            blockedAuth,
            blockedOperator,
            _watermarks.CommittedBytes,
            _watermarks.CommittedCount,
            paused,
            completed,
            blockReason);

    /// <summary>Records staged bytes whenever extraction completes, without changing extraction behavior.</summary>
    private sealed class WatermarkTrackingExtractor : IExtractor
    {
        private readonly IExtractor _inner;
        private readonly WatermarkScheduler _watermarks;

        public WatermarkTrackingExtractor(IExtractor inner, WatermarkScheduler watermarks)
        {
            _inner = inner;
            _watermarks = watermarks;
        }

        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
        {
            var result = await _inner.ExtractAsync(request, cancellationToken);
            if (result.Outcome == ExtractionOutcome.Completed && result.NormalizedText is not null)
            {
                _watermarks.RecordStaged(request.SourcePath, Encoding.UTF8.GetByteCount(result.NormalizedText));
            }

            return result;
        }
    }
}
