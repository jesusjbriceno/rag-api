using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rag.HistoricalLoader.Contracts;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Core.Persistence;
using Rag.HistoricalLoader.Engine.Pipeline;

namespace Rag.HistoricalLoader.Engine.Control;

/// <summary>
/// Composition facts of one supervised engine process. The engine-instance identity is new per process and is
/// recorded durably; every other value is a recorded engine policy or path, never a wire input.
/// </summary>
public sealed record ControlServiceOptions
{
    /// <summary>The engine-instance identity of this process. A new process records a new one.</summary>
    public required string EngineInstanceId { get; init; }

    /// <summary>The engine version recorded on a started run.</summary>
    public string EngineVersion { get; init; } = "0.1.0";

    /// <summary>The target collection of a started run.</summary>
    public string CollectionId { get; init; } = "legacy";

    /// <summary>
    /// How long host shutdown waits for the supervised pipeline to reach a durable pause boundary. The
    /// recorded default is 30 seconds; a timeout leaves recovery work instead of a fabricated pause.
    /// </summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The largest approved batch this engine starts in one command. The proof-run capacity decision is a
    /// deferred operator decision; this recorded bound is what makes a start fail closed instead of unbounded.
    /// </summary>
    public int MaxStartDocumentCount { get; init; } = DefaultMaxStartDocumentCount;

    /// <summary>
    /// The loader database the read-only batch resolver opens. An engine without it cannot resolve an approved
    /// batch, so every start is rejected rather than run from an unverified selection.
    /// </summary>
    public string? DatabasePath { get; init; }

    /// <summary>
    /// The secret-free configuration snapshot recorded on a started run. The host composes it from the
    /// pipeline bounds it actually applied; a service composed without bounds records an empty object.
    /// </summary>
    public string? ConfigurationSnapshot { get; init; }

    /// <summary>The recorded default batch bound.</summary>
    public const int DefaultMaxStartDocumentCount = 10_000;
}

/// <summary>The outcome of a bounded host drain.</summary>
public enum ControlDrainStatus
{
    /// <summary>The supervised run reached a durable boundary (paused, completed, or blocked) in time.</summary>
    Drained = 0,

    /// <summary>The drain deadline expired; the run keeps its durable in-flight state and needs recovery.</summary>
    TimedOut = 1,
}

/// <summary>
/// One drain result: whether the run settled inside the deadline, and the durable observed state it settled
/// at (or <see langword="null"/> when the engine supervises no run).
/// </summary>
public sealed record ControlDrainResult(ControlDrainStatus Status, RunObservedState? ObservedState);

/// <summary>
/// The engine-side control handler behind the unchanged dispatcher seam. It owns the engine's own policy —
/// approved-batch resolution, one active run per installation, durable command receipts, and the bounded,
/// allowlisted projections — and composes the existing <see cref="HistoricalPipeline"/> for every lifecycle,
/// retry, pause, and resume decision instead of duplicating any of it.
/// </summary>
/// <remarks>
/// Command serialization covers only short validation and store transactions. The supervised pipeline runs
/// outside that gate, so a pause is acknowledged while the pipeline still drains and the request thread never
/// becomes the pipeline.
/// </remarks>
public sealed class ControlService : IControlCommandHandler, IAsyncDisposable
{
    private readonly SqliteStore _store;
    private readonly SqliteControlStore _control;
    private readonly SqliteRunStore _runStore;
    private readonly HistoricalPipeline _pipeline;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _supervision = new();
    private ControlSampleBatchResolver? _batches;
    private Task? _pipelineTask;
    private Guid? _supervisedRunId;

    public ControlService(
        SqliteStore store,
        SqliteControlStore control,
        SqliteRunStore runStore,
        HistoricalPipeline pipeline,
        ControlServiceOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The recorded composition facts of this process.</summary>
    public ControlServiceOptions Options { get; }

    /// <summary>The run this engine supervises, or <see langword="null"/> when no start was accepted.</summary>
    public Guid? SupervisedRunId
    {
        get
        {
            lock (_supervision)
            {
                return _supervisedRunId;
            }
        }
    }

    /// <summary>
    /// Records the durable engine-instance identity of this process. It refreshes the identity only: a restart
    /// never resets runs, documents, receipts, or attempt counters, and startup never dispatches ingestion.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
        => _control.RecordEngineInstanceAsync(Options.EngineInstanceId, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<ControlDispatchOutcome> HandleAsync(
        ControlRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Operation switch
        {
            ControlOperations.Hello => await HelloAsync(cancellationToken).ConfigureAwait(false),
            ControlOperations.Start => await StartAsync(request, cancellationToken).ConfigureAwait(false),
            ControlOperations.Pause => await PauseAsync(request, cancellationToken).ConfigureAwait(false),
            ControlOperations.Resume => await ResumeAsync(request, cancellationToken).ConfigureAwait(false),
            ControlOperations.GetState => await GetStateAsync(request, cancellationToken).ConfigureAwait(false),
            ControlOperations.GetDocuments => await GetDocumentsAsync(request, cancellationToken).ConfigureAwait(false),
            ControlOperations.GetEvents => await GetEventsAsync(request, cancellationToken).ConfigureAwait(false),
            _ => ControlDispatchOutcome.Rejected(ControlErrorCodes.UnknownOperation),
        };
    }

    /// <summary>
    /// Requests a safe pause and waits, within <see cref="ControlServiceOptions.DrainTimeout"/>, for the
    /// supervised pipeline to reach a durable boundary. A timeout leaves the durable in-flight state alone:
    /// nothing is reported paused or loaded that the durable rows do not already say.
    /// </summary>
    public async Task<ControlDrainResult> DrainAsync(CancellationToken cancellationToken = default)
    {
        var runId = SupervisedRunId;
        Task? pipeline;
        lock (_supervision)
        {
            pipeline = _pipelineTask;
        }

        if (runId is null)
        {
            return new ControlDrainResult(ControlDrainStatus.Drained, null);
        }

        // Commit the pause intent before waiting so the durable rows already say "pause_requested" while the
        // pipeline drains, and a kill inside the drain leaves recovery work instead of a fabricated pause.
        await CommitIntentAsync(Guid.NewGuid().ToString(), runId.Value.ToString(), RunDesiredState.PauseRequested, RunObservedState.Pausing, cancellationToken)
            .ConfigureAwait(false);

        if (pipeline is not null && !pipeline.IsCompleted)
        {
            try
            {
                await Task.WhenAny(pipeline, Task.Delay(Options.DrainTimeout, cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The caller cancelled the wait: report the durable state rather than claiming a drain.
            }
        }

        var observed = await TryReadObservedStateAsync(runId.Value, cancellationToken).ConfigureAwait(false);
        var settled = observed is null
            or RunObservedState.Paused
            or RunObservedState.Completed
            or RunObservedState.BlockedAuth
            or RunObservedState.BlockedOperatorAction;
        return new ControlDrainResult(
            settled ? ControlDrainStatus.Drained : ControlDrainStatus.TimedOut,
            observed);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task? pipeline;
        lock (_supervision)
        {
            pipeline = _pipelineTask;
            _pipelineTask = null;
        }

        _lifetime.Cancel();
        if (pipeline is not null)
        {
            try
            {
                await pipeline.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A cancelled pipeline ends here; its durable rows already describe the boundary it reached.
            }
            catch (Exception)
            {
                // A faulted pipeline was already surfaced as a safe rejection by the command that started it.
            }
        }

        if (_batches is not null)
        {
            await _batches.DisposeAsync().ConfigureAwait(false);
            _batches = null;
        }

        _lifetime.Dispose();
        _commandGate.Dispose();
    }

    // --- commands -----------------------------------------------------------

    private async Task<ControlDispatchOutcome> HelloAsync(CancellationToken cancellationToken)
    {
        try
        {
            var installationId = await _store.GetInstallationIdAsync(cancellationToken).ConfigureAwait(false);
            return ControlDispatchOutcome.Ok(new HelloResult(
                ControlProtocol.SupportedVersions,
                ControlCapabilities.All,
                installationId.ToString(),
                Options.EngineInstanceId,
                new ControlLimitsPayload(
                    ControlProtocol.Limits.MaxFrameBytes,
                    ControlProtocol.Limits.MaxJsonDepth,
                    ControlProtocol.Limits.MaxPageSize)));
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            return ControlDispatchOutcome.Rejected(ControlErrorCodes.CommandConflict);
        }
    }

    /// <summary>
    /// Starts a run for an operator-approved batch. The client sends only an opaque batch id: the engine
    /// resolves the gate decision, the selected members, and the secret-free configuration itself, then commits
    /// the run, its documents, the operator audit row, and the durable receipt in one transaction. Nothing is
    /// acknowledged until that commit returned, and a replay returns the durable receipt without a second run.
    /// </summary>
    private async Task<ControlDispatchOutcome> StartAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        var payload = Bind<StartRequest>(request.Payload);
        if (payload is null
            || !Guid.TryParse(payload.CommandId, out var commandId)
            || !Guid.TryParse(payload.BatchId, out var batchId)
            || !Guid.TryParse(payload.RunId, out var runId))
        {
            return ControlDispatchOutcome.Rejected(ControlErrorCodes.MalformedRequest);
        }

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var batch = await ResolveBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
            if (batch is null
                || !batch.GatePassed
                || batch.Documents.Count == 0
                || batch.Documents.Count > Options.MaxStartDocumentCount)
            {
                // Unknown, ungated, empty, and over-capacity batches are one stable rejection: nothing was
                // read as approved and nothing will be written.
                return ControlDispatchOutcome.Rejected(ControlErrorCodes.CommandConflict);
            }

            var run = new Run(
                runId,
                batch.SampleSetId,
                Options.CollectionId,
                RunDesiredState.Running,
                RunObservedState.Running,
                Options.EngineVersion,
                Options.ConfigurationSnapshot ?? "{}",
                DateTimeOffset.UtcNow);
            var plan = new StartCommandPlan(
                commandId,
                run,
                batch.Documents
                    .Select(document => new StartDocument(
                        StartDocumentId(runId, document.CandidateId), document.CandidateId, document.SourcePath))
                    .ToArray());

            CommandCommitResult commit;
            try
            {
                commit = await _control.CommitStartAsync(plan, DecideStart, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsStorageFault(exception))
            {
                return ControlDispatchOutcome.Rejected(ControlErrorCodes.CommandConflict);
            }

            switch (commit.Outcome)
            {
                case CommandCommitOutcome.Committed:
                    Supervise(commit.Run!.Id);
                    return ControlDispatchOutcome.Accepted(ControlProjections.Receipt(commit.Receipt!));

                case CommandCommitOutcome.Replayed:
                    // The command is already durable: its receipt is returned and no pipeline is started again.
                    return ControlDispatchOutcome.Accepted(ControlProjections.Receipt(commit.Receipt!));

                default:
                    return ControlDispatchOutcome.Rejected(ControlErrorCodes.CommandConflict);
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    /// <summary>
    /// Commits a pause intent with its operator audit row and durable receipt. The acknowledgement is the
    /// durable intent, never a confirmed checkpoint, so it never waits for the drain.
    /// </summary>
    private async Task<ControlDispatchOutcome> PauseAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        var payload = Bind<PauseRequest>(request.Payload);
        if (payload is null)
        {
            return ControlDispatchOutcome.Rejected(ControlErrorCodes.MalformedRequest);
        }

        return await CommitIntentAsync(
            payload.CommandId, payload.RunId, RunDesiredState.PauseRequested, RunObservedState.Pausing, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Commits a resume intent and continues only eligible work. Reconciliation and the attempt ceiling stay in
    /// the existing pipeline: a resume preserves terminal outcomes and their exhausted attempt counters, and a
    /// blocked run is refused by <see cref="DecideDesiredState"/> because the wire carries no bypass flag.
    /// </summary>
    private async Task<ControlDispatchOutcome> ResumeAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        var payload = Bind<ResumeRequest>(request.Payload);
        if (payload is null)
        {
            return ControlDispatchOutcome.Rejected(ControlErrorCodes.MalformedRequest);
        }

        var outcome = await CommitIntentAsync(
            payload.CommandId, payload.RunId, RunDesiredState.Running, RunObservedState.Running, cancellationToken)
            .ConfigureAwait(false);

        if (outcome.Status == ControlStatuses.Accepted && Guid.TryParse(payload.RunId, out var runId))
        {
            Supervise(runId);
        }

        return outcome;
    }

    private async Task<ControlDispatchOutcome> CommitIntentAsync(
        string commandIdText,
        string runIdText,
        RunDesiredState desiredState,
        RunObservedState observedState,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(commandIdText, out var commandId) || !Guid.TryParse(runIdText, out var runId))
        {
            return ControlDispatchOutcome.Rejected(ControlErrorCodes.MalformedRequest);
        }

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CommandCommitResult commit;
            try
            {
                commit = await _control.CommitDesiredStateAsync(
                    new DesiredStateCommandPlan(commandId, runId, desiredState, observedState),
                    view => DecideDesiredState(view, desiredState),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsStorageFault(exception))
            {
                return ControlDispatchOutcome.Rejected(ControlErrorCodes.CommandConflict);
            }

            return commit.Outcome switch
            {
                CommandCommitOutcome.Committed or CommandCommitOutcome.Replayed
                    => ControlDispatchOutcome.Accepted(ControlProjections.Receipt(commit.Receipt!)),
                _ => ControlDispatchOutcome.Rejected(ControlErrorCodes.CommandConflict),
            };
        }
        finally
        {
            _commandGate.Release();
        }
    }

    // --- projections --------------------------------------------------------

    private async Task<ControlDispatchOutcome> GetStateAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        var payload = Bind<GetStateRequest>(request.Payload) ?? new GetStateRequest();
        Guid? runId = null;
        if (payload.RunId is not null)
        {
            if (!Guid.TryParse(payload.RunId, out var parsed))
            {
                return ControlDispatchOutcome.Rejected(ControlErrorCodes.MalformedRequest);
            }

            runId = parsed;
        }

        try
        {
            var snapshot = await _control.GetControlSnapshotAsync(runId, cancellationToken).ConfigureAwait(false);
            return ControlDispatchOutcome.Ok(ControlProjections.State(snapshot, Options.EngineInstanceId));
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            return ControlDispatchOutcome.Rejected(ControlErrorCodes.CommandConflict);
        }
    }

    private async Task<ControlDispatchOutcome> GetDocumentsAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        var payload = Bind<GetDocumentsRequest>(request.Payload);
        if (payload is null || !Guid.TryParse(payload.RunId, out var runId))
        {
            return ControlDispatchOutcome.Rejected(ControlErrorCodes.MalformedRequest);
        }

        DocumentPageCursor? after = null;
        if (payload.AfterDocumentId is not null)
        {
            if (!ControlDocumentCursor.TryDecode(payload.AfterDocumentId, out var cursor))
            {
                // An unreadable cursor cannot be served contiguously: the client must resynchronise.
                return ControlDispatchOutcome.Rejected(ControlErrorCodes.ResyncRequired);
            }

            after = cursor.ToPageCursor();
        }

        try
        {
            var page = await _control.GetDocumentPageAsync(runId, payload.Limit, after, cancellationToken).ConfigureAwait(false);
            return page.Outcome == ControlPageOutcome.Ok
                ? ControlDispatchOutcome.Ok(ControlProjections.Documents(page.Page!))
                : ControlDispatchOutcome.Rejected(PageErrorCode(page.Outcome));
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            return ControlDispatchOutcome.Rejected(ControlErrorCodes.CommandConflict);
        }
    }

    private async Task<ControlDispatchOutcome> GetEventsAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        var payload = Bind<GetEventsRequest>(request.Payload);
        if (payload is null)
        {
            return ControlDispatchOutcome.Rejected(ControlErrorCodes.MalformedRequest);
        }

        Guid? runId = null;
        if (payload.RunId is not null)
        {
            if (!Guid.TryParse(payload.RunId, out var parsed))
            {
                return ControlDispatchOutcome.Rejected(ControlErrorCodes.MalformedRequest);
            }

            runId = parsed;
        }

        if (!TryParseEventCursor(payload.AfterEventId, out var afterEventId))
        {
            // The client's acknowledged position is unreadable, so the feed cannot continue from it.
            return ControlDispatchOutcome.Rejected(ControlErrorCodes.ResyncRequired);
        }

        long? throughEventId = null;
        if (payload.ThroughEventId is not null)
        {
            if (!TryParseEventCursor(payload.ThroughEventId, out var parsedThrough))
            {
                return ControlDispatchOutcome.Rejected(ControlErrorCodes.MalformedRequest);
            }

            throughEventId = parsedThrough;
        }

        try
        {
            var page = await _control
                .GetEventPageAsync(payload.Limit, afterEventId, throughEventId, runId, cancellationToken)
                .ConfigureAwait(false);
            return page.Outcome == ControlPageOutcome.Ok
                ? ControlDispatchOutcome.Ok(ControlProjections.Events(page.Page!))
                : ControlDispatchOutcome.Rejected(PageErrorCode(page.Outcome));
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            return ControlDispatchOutcome.Rejected(ControlErrorCodes.CommandConflict);
        }
    }

    // --- engine policy ------------------------------------------------------

    /// <summary>
    /// The one-active-run and one-run-per-command policy, decided inside the command transaction: an existing
    /// run is never merged or duplicated, and a second run is refused while another run is active. A rejected
    /// start writes nothing.
    /// </summary>
    private static PreconditionDecision DecideStart(StartPreconditionView view)
        => view.RunExists || view.HasActiveRun
            ? PreconditionDecision.Reject(ControlStoreErrorCodes.CommandConflict)
            : PreconditionDecision.Accept;

    /// <summary>
    /// Pause/resume eligibility, decided inside the command transaction. A settled or intervention-blocked run
    /// is not paused, and a blocked run cannot be resumed by a wire flag: blocked conditions need engine-side
    /// validation of the correction.
    /// </summary>
    private static PreconditionDecision DecideDesiredState(DesiredStatePreconditionView view, RunDesiredState desiredState)
    {
        if (view.CurrentRun is not { } run)
        {
            return PreconditionDecision.Reject(ControlStoreErrorCodes.CommandConflict);
        }

        var blocked = run.ObservedState is RunObservedState.BlockedAuth or RunObservedState.BlockedOperatorAction;
        return desiredState switch
        {
            RunDesiredState.PauseRequested when blocked || run.ObservedState == RunObservedState.Completed
                => PreconditionDecision.Reject(ControlStoreErrorCodes.CommandConflict),
            RunDesiredState.Running when blocked
                => PreconditionDecision.Reject(ControlStoreErrorCodes.CommandConflict),
            _ => PreconditionDecision.Accept,
        };
    }

    private async Task<ResolvedSampleBatch?> ResolveBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Options.DatabasePath))
        {
            // Without the loader database an approved batch cannot be resolved: fail closed rather than start
            // a run from an unverified selection.
            return null;
        }

        _batches ??= new ControlSampleBatchResolver(Options.DatabasePath);
        try
        {
            return await _batches.ResolveAsync(batchId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// The durable identity of a start document, derived from the run and the candidate it selects. Deriving
    /// it — instead of minting a fresh identifier per attempt — makes the same command reduce to the same
    /// normalized fingerprint, so replaying an accepted start returns its durable receipt instead of a
    /// fingerprint conflict. Two runs and two candidates can never map to the same document.
    /// </summary>
    private static Guid StartDocumentId(Guid runId, Guid candidateId)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{runId}:{candidateId}")).AsSpan(0, 16));

    /// <summary>
    /// Supervises one pipeline task per run. Only one pipeline runs at a time: a new task awaits the previous
    /// one before it claims work, so a concurrent command, a reconnect, or a replayed start can never run
    /// ingestion twice. The task is never awaited by the command that scheduled it.
    /// </summary>
    private void Supervise(Guid runId)
    {
        Task? previous;
        CancellationToken token;
        lock (_supervision)
        {
            previous = _pipelineTask;
            _supervisedRunId = runId;
            token = _lifetime.Token;
            _pipelineTask = Task.Run(() => RunPipelineAsync(runId, previous, token), CancellationToken.None);
        }
    }

    private async Task RunPipelineAsync(Guid runId, Task? previous, CancellationToken cancellationToken)
    {
        if (previous is not null)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The previous run's outcome was already durable; the next run never inherits its failure.
            }
        }

        await _pipeline.RunAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RunObservedState?> TryReadObservedStateAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            return (await _runStore.GetRunAsync(runId, cancellationToken).ConfigureAwait(false))?.ObservedState;
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            return null;
        }
    }

    // --- shared helpers -----------------------------------------------------

    /// <summary>
    /// The stable wire code of a page outcome. An out-of-range request is a malformed request; every other
    /// non-answer is a resynchronisation request.
    /// </summary>
    private static string PageErrorCode(ControlPageOutcome outcome) => outcome switch
    {
        ControlPageOutcome.InvalidRequest => ControlErrorCodes.MalformedRequest,
        _ => ControlErrorCodes.ResyncRequired,
    };

    private static bool TryParseEventCursor(string? cursor, out long eventId)
    {
        eventId = 0L;
        return cursor is null
            || long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out eventId);
    }

    private static T? Bind<T>(JsonElement? payload) where T : class
    {
        if (payload is not { } element)
        {
            return null;
        }

        try
        {
            return element.Deserialize<T>(ControlWire.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The durable-store faults that are surfaced as a stable rejection. Cancellation is deliberately not one
    /// of them, and neither is a programming error: a fault the engine caused must stay visible.
    /// </summary>
    private static bool IsStorageFault(Exception exception) => exception switch
    {
        SqliteException => true,
        ObjectDisposedException => true,
        IOException => true,
        UnauthorizedAccessException => true,
        ControlMeasurementBoundaryException => true,
        _ => false,
    };
}
