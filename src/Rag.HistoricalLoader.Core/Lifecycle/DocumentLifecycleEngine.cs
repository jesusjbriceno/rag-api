using System.Security.Cryptography;
using System.Text;
using Rag.HistoricalLoader.Core.Extraction;

namespace Rag.HistoricalLoader.Core.Lifecycle;

/// <summary>
/// Drives one document through the durable lifecycle state machine against a fake/real extractor and a
/// fake/real historical API. Attempt counters are persisted by <see cref="DispatchAsync"/> before dispatch
/// so a crash after dispatch never re-runs a mutation past the three-attempt ceiling. This Unit 6 engine
/// uses the fake collaborators and proves the checkpoint, retry, pause, and reconciliation semantics.
/// </summary>
public sealed class DocumentLifecycleEngine
{
    private readonly IExtractor _extractor;
    private readonly IHistoricalApiClient _api;
    private readonly SqliteRunStore _runStore;

    public DocumentLifecycleEngine(IExtractor extractor, IHistoricalApiClient api, SqliteRunStore runStore)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
    }

    /// <summary>
    /// Records one dispatched attempt (persisted before the operation runs) and then invokes
    /// <paramref name="operation"/>. A cancelled call before dispatch consumes nothing.
    /// </summary>
    public async Task<(ApiOperationResult Result, RunDocument Document)> DispatchAsync(
        RunDocument document,
        DocumentState stage,
        Func<CancellationToken, Task<ApiOperationResult>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var counter = document.AttemptsFor(stage).RecordDispatch();
        var updated = document.WithAttempts(stage, counter) with { UpdatedAt = DateTimeOffset.UtcNow };

        // Persist the attempt BEFORE dispatch: a crash after dispatch can never re-run the mutation past the ceiling.
        var persisted = await _runStore.SaveAsync(updated, "dispatch", null, counter.DispatchedAttempts, null, cancellationToken);

        var result = await operation(cancellationToken);
        return (result, persisted);
    }

    public async Task<RunDocument> ProcessDocumentAsync(RunDocument document, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (next, progressed) = await StepAsync(document, cancellationToken);
            document = next;

            if (!progressed || DocumentLifecycle.IsTerminal(document.State))
            {
                return document;
            }

            if (document.State is DocumentState.BlockedAuth or DocumentState.BlockedOperatorAction or DocumentState.Interrupted)
            {
                return document;
            }
        }
    }

    /// <summary>Reconciles in-flight/transient work to its last durable safe stage after a forced kill.</summary>
    public async Task<IReadOnlyList<RunDocument>> ReconcileAsync(Guid runId, CancellationToken cancellationToken)
    {
        var documents = await _runStore.GetRunDocumentsAsync(runId, cancellationToken);
        var changed = new List<RunDocument>();

        foreach (var document in documents)
        {
            var recovered = document;

            if (document.State == DocumentState.Staged && string.IsNullOrEmpty(document.NormalizedTextHash))
            {
                // A staged reference with no durable hash is corrupt.
                recovered = document with { State = DocumentState.Pending, UpdatedAt = DateTimeOffset.UtcNow };
            }
            else if (!DocumentLifecycle.HasDurableReceipt(document.State))
            {
                recovered = document with { State = DocumentLifecycle.Recover(document.State), UpdatedAt = DateTimeOffset.UtcNow };
            }

            if (recovered.State != document.State)
            {
                changed.Add(await _runStore.SaveAsync(recovered, "reconcile", null, 0, null, cancellationToken));
            }
        }

        return changed;
    }

    /// <summary>Reads the next durable pending claim without mutating state; honours the pause signal.</summary>
    public async Task<DocumentClaim?> TryClaimNextAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await _runStore.GetRunAsync(runId, cancellationToken);
        if (run is null || run.DesiredState != RunDesiredState.Running)
        {
            return null;
        }

        var claims = await _runStore.ClaimPendingAsync(runId, 1, cancellationToken);
        return claims.Count > 0 ? claims[0] : null;
    }

    public async Task<Run> PauseAsync(Guid runId, CancellationToken cancellationToken)
    {
        await _runStore.SetRunDesiredStateAsync(runId, RunDesiredState.PauseRequested, cancellationToken);
        await _runStore.SetRunObservedStateAsync(runId, RunObservedState.Pausing, cancellationToken);
        return (await _runStore.GetRunAsync(runId, cancellationToken))!;
    }

    public async Task<bool> CanReportPausedAsync(Guid runId, CancellationToken cancellationToken)
    {
        var documents = await _runStore.GetRunDocumentsAsync(runId, cancellationToken);
        return documents.All(IsDurableSafe);
    }

    public async Task<Run> CompletePauseAsync(Guid runId, CancellationToken cancellationToken)
    {
        await _runStore.SetRunObservedStateAsync(runId, RunObservedState.Paused, cancellationToken);
        return (await _runStore.GetRunAsync(runId, cancellationToken))!;
    }

    private async Task<(RunDocument Document, bool Progressed)> StepAsync(RunDocument document, CancellationToken cancellationToken)
    {
        switch (document.State)
        {
            case DocumentState.Pending:
                return (await AdvanceAsync(document, DocumentState.Snapshotting, cancellationToken), true);

            case DocumentState.Snapshotting:
                return (await AdvanceAsync(document, DocumentState.Extracting, cancellationToken), true);

            case DocumentState.Extracting:
                return (await ExtractAsync(document, cancellationToken), true);

            case DocumentState.Staged:
                return (await AdvanceAsync(document, DocumentState.Reserving, cancellationToken), true);

            case DocumentState.Reserving:
                return (await RunNetworkStageAsync(
                    document,
                    DocumentState.Reserving,
                    DocumentState.Uploading,
                    d => new ApiOperation($"{d.Id}:reserve", d.SourceDocumentKey, d.NormalizedTextHash),
                    (op, ct) => _api.ReserveAsync(op, ct),
                    (d, remoteId) => d with { RemoteUploadId = remoteId },
                    cancellationToken), true);

            case DocumentState.Uploading:
                return (await RunNetworkStageAsync(
                    document,
                    DocumentState.Uploading,
                    DocumentState.Committing,
                    d => new ApiOperation($"{d.Id}:upload", d.SourceDocumentKey, d.NormalizedTextHash),
                    (op, ct) => _api.UploadAsync(op, ct),
                    (d, remoteId) => d with { RemoteUploadId = remoteId },
                    cancellationToken), true);

            case DocumentState.Committing:
                return (await RunNetworkStageAsync(
                    document,
                    DocumentState.Committing,
                    DocumentState.RemotePending,
                    d => new ApiOperation($"{d.Id}:commit", d.SourceDocumentKey, d.NormalizedTextHash),
                    (op, ct) => _api.CommitAsync(op, ct),
                    (d, remoteId) => d with { RemoteOperationId = remoteId },
                    cancellationToken), true);

            case DocumentState.RemotePending:
                return await PollAsync(document, cancellationToken);

            default:
                return (document, false);
        }
    }

    private async Task<RunDocument> AdvanceAsync(RunDocument document, DocumentState nextState, CancellationToken cancellationToken)
        => await _runStore.SaveAsync(
            document with { State = nextState, UpdatedAt = DateTimeOffset.UtcNow },
            "advance",
            null,
            0,
            null,
            cancellationToken);

    private async Task<RunDocument> ExtractAsync(RunDocument document, CancellationToken cancellationToken)
    {
        var request = new ExtractionRequest(document.SourceDocumentKey, "txt");
        var result = await _extractor.ExtractAsync(request, cancellationToken);

        if (result.Outcome == ExtractionOutcome.Completed)
        {
            var normalizedText = result.NormalizedText ?? string.Empty;

            // The staged byte measurement commits in the SAME transaction as the durable staged row, so no
            // later processing point that can crash runs while the staged accounting is missing, and a
            // restart reconstructs the staged capacity and committed watermark from durable rows alone.
            return await _runStore.SaveStagedAsync(
                document, ComputeSha256(normalizedText), Encoding.UTF8.GetByteCount(normalizedText), cancellationToken);
        }

        return await _runStore.SaveAsync(
            document with
            {
                State = DocumentLifecycle.OnDocumentFailure(),
                TerminalClassification = result.ErrorCode,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            "skipped",
            result.ErrorCode,
            0,
            null,
            cancellationToken);
    }

    private async Task<RunDocument> RunNetworkStageAsync(
        RunDocument document,
        DocumentState stage,
        DocumentState nextState,
        Func<RunDocument, ApiOperation> buildOperation,
        Func<ApiOperation, CancellationToken, Task<ApiOperationResult>> call,
        Func<RunDocument, string?, RunDocument> applyRemoteId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var operation = buildOperation(document);
            var (result, afterDispatch) = await DispatchAsync(document, stage, ct => call(operation, ct), cancellationToken);
            document = afterDispatch;

            switch (result.Outcome)
            {
                case ApiOutcome.Success:
                    return await AdvanceAsync(applyRemoteId(document, result.RemoteId), nextState, cancellationToken);

                case ApiOutcome.TransientFailure:
                case ApiOutcome.UnknownOutcome:
                    var counter = document.AttemptsFor(stage);
                    var failed = DocumentLifecycle.OnNetworkFailure(stage, counter);

                    if (failed == DocumentState.RetryExhaustedNetwork)
                    {
                        return await _runStore.SaveAsync(
                            document with
                            {
                                State = failed,
                                TerminalClassification = "retry_exhausted",
                                UpdatedAt = DateTimeOffset.UtcNow,
                            },
                            "retry_exhausted",
                            result.ErrorCode,
                            counter.DispatchedAttempts,
                            null,
                            cancellationToken);
                    }

                    document = await _runStore.SaveAsync(
                        document with { State = failed, UpdatedAt = DateTimeOffset.UtcNow },
                        "network_failure",
                        result.ErrorCode,
                        counter.DispatchedAttempts,
                        null,
                        cancellationToken);

                    document = await _runStore.SaveAsync(
                        document with { State = DocumentLifecycle.ResumeFromRetry(stage), UpdatedAt = DateTimeOffset.UtcNow },
                        "retry_resume",
                        null,
                        counter.DispatchedAttempts,
                        null,
                        cancellationToken);

                    continue;

                case ApiOutcome.AuthFailure:
                    return await _runStore.SaveAsync(
                        document with { State = DocumentLifecycle.BlockedAuth(), UpdatedAt = DateTimeOffset.UtcNow },
                        "blocked_auth",
                        result.ErrorCode,
                        document.AttemptsFor(stage).DispatchedAttempts,
                        null,
                        cancellationToken);

                case ApiOutcome.ContractFailure:
                    return await _runStore.SaveAsync(
                        document with { State = DocumentLifecycle.BlockedOperatorAction(), UpdatedAt = DateTimeOffset.UtcNow },
                        "blocked_contract",
                        result.ErrorCode,
                        document.AttemptsFor(stage).DispatchedAttempts,
                        null,
                        cancellationToken);

                default:
                    throw new InvalidOperationException($"Unknown API outcome '{result.Outcome}'.");
            }
        }
    }

    private async Task<(RunDocument Document, bool Progressed)> PollAsync(RunDocument document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var operation = new ApiOperation($"{document.Id}:poll", document.SourceDocumentKey, document.NormalizedTextHash);

        // Persist the poll attempt BEFORE dispatch so a crash after dispatch can never re-run the poll past
        // the three-attempt ceiling (Unit 10 corrective finding: durable poll accounting).
        var counter = document.AttemptsFor(DocumentState.RemotePending).RecordDispatch();
        var reserved = document.WithAttempts(DocumentState.RemotePending, counter) with { UpdatedAt = DateTimeOffset.UtcNow };
        var afterDispatch = await _runStore.SaveAsync(reserved, "poll_dispatch", null, counter.DispatchedAttempts, null, cancellationToken);

        var result = await _api.PollAsync(operation, cancellationToken);

        switch (result.Outcome)
        {
            case ApiOutcome.Success:
                if (result.Pending)
                {
                    // A successful poll that reports still-processing is not a failed retry: roll the reservation back.
                    var rolledBack = await _runStore.SaveAsync(
                        document with { UpdatedAt = DateTimeOffset.UtcNow },
                        "poll_pending",
                        null,
                        0,
                        null,
                        cancellationToken);
                    return (rolledBack, false);
                }

                var loaded = await _runStore.SaveAsync(
                    afterDispatch with
                    {
                        State = DocumentState.Loaded,
                        RemoteOperationId = result.RemoteId ?? afterDispatch.RemoteOperationId,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    },
                    "loaded",
                    null,
                    counter.DispatchedAttempts,
                    null,
                    cancellationToken);
                return (loaded, true);

            case ApiOutcome.TransientFailure:
            case ApiOutcome.UnknownOutcome:
                var failed = DocumentLifecycle.OnNetworkFailure(DocumentState.RemotePending, counter);

                if (failed == DocumentState.RetryExhaustedNetwork)
                {
                    var exhausted = await _runStore.SaveAsync(
                        afterDispatch with
                        {
                            State = failed,
                            TerminalClassification = "retry_exhausted",
                            UpdatedAt = DateTimeOffset.UtcNow,
                        },
                        "retry_exhausted",
                        result.ErrorCode,
                        counter.DispatchedAttempts,
                        null,
                        cancellationToken);
                    return (exhausted, true);
                }

                await _runStore.SaveAsync(
                    afterDispatch with { State = failed, UpdatedAt = DateTimeOffset.UtcNow },
                    "poll_failure",
                    result.ErrorCode,
                    counter.DispatchedAttempts,
                    null,
                    cancellationToken);

                var resumed = await _runStore.SaveAsync(
                    afterDispatch with { State = DocumentState.RemotePending, UpdatedAt = DateTimeOffset.UtcNow },
                    "poll_resume",
                    null,
                    counter.DispatchedAttempts,
                    null,
                    cancellationToken);
                return (resumed, true);

            case ApiOutcome.AuthFailure:
                var blockedAuth = await _runStore.SaveAsync(
                    afterDispatch with { State = DocumentLifecycle.BlockedAuth(), UpdatedAt = DateTimeOffset.UtcNow },
                    "blocked_auth",
                    result.ErrorCode,
                    counter.DispatchedAttempts,
                    null,
                    cancellationToken);
                return (blockedAuth, true);

            case ApiOutcome.ContractFailure:
                var blockedContract = await _runStore.SaveAsync(
                    afterDispatch with { State = DocumentLifecycle.BlockedOperatorAction(), UpdatedAt = DateTimeOffset.UtcNow },
                    "blocked_contract",
                    result.ErrorCode,
                    counter.DispatchedAttempts,
                    null,
                    cancellationToken);
                return (blockedContract, true);

            default:
                throw new InvalidOperationException($"Unknown poll outcome '{result.Outcome}'.");
        }
    }

    private static bool IsDurableSafe(RunDocument document)
    {
        if (document.State == DocumentState.Staged)
        {
            return !string.IsNullOrEmpty(document.NormalizedTextHash);
        }

        return DocumentLifecycle.HasDurableReceipt(document.State);
    }

    private static string ComputeSha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
