namespace Rag.HistoricalLoader.Core.Lifecycle;

/// <summary>
/// Durable per-document lifecycle states. Terminal outcomes are <see cref="Loaded"/>,
/// <see cref="SkippedDocumentError"/>, and <see cref="RetryExhaustedNetwork"/>. The remaining
/// states are transient or intervention-required and are never reported as a confirmed completion.
/// </summary>
public enum DocumentState
{
    Pending = 0,
    Snapshotting = 1,
    Extracting = 2,
    Staged = 3,
    Reserving = 4,
    Uploading = 5,
    Committing = 6,
    RemotePending = 7,
    Loaded = 8,
    SkippedDocumentError = 9,
    RetryWait = 10,
    RetryExhaustedNetwork = 11,
    Interrupted = 12,
    BlockedAuth = 13,
    BlockedOperatorAction = 14,
}

/// <summary>Operator-intended run state.</summary>
public enum RunDesiredState
{
    Running = 0,
    PauseRequested = 1,
}

/// <summary>Durably observed run state.</summary>
public enum RunObservedState
{
    Running = 0,
    Pausing = 1,
    Paused = 2,
    BlockedAuth = 3,
    BlockedOperatorAction = 4,
    Completed = 5,
}

/// <summary>
/// A single ingestion run: identity, sample/batch, target collection, desired/observed state, engine
/// version, a secret-free configuration snapshot, and timestamps. No credential material is stored.
/// </summary>
public sealed record Run(
    Guid Id,
    Guid? SampleId,
    string CollectionId,
    RunDesiredState DesiredState,
    RunObservedState ObservedState,
    string EngineVersion,
    string ConfigurationSnapshot,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt = null,
    DateTimeOffset? CheckpointAt = null);

/// <summary>
/// One selected document's durable lifecycle record. Attempt counters are kept per network operation
/// (reserve/upload/commit/poll) so a later generation can be audited without losing the three-attempt
/// ceiling for an earlier operation.
/// </summary>
public sealed record RunDocument(
    Guid Id,
    Guid RunId,
    Guid CandidateId,
    string SourceDocumentKey,
    DocumentState State,
    int ReserveAttempts = 0,
    int UploadAttempts = 0,
    int CommitAttempts = 0,
    int PollAttempts = 0,
    string? ExtractionHash = null,
    string? NormalizedTextHash = null,
    string? RemoteUploadId = null,
    string? RemoteDocumentId = null,
    string? RemoteVersionId = null,
    string? RemoteOperationId = null,
    string? TerminalClassification = null,
    DateTimeOffset CreatedAt = default,
    DateTimeOffset UpdatedAt = default)
{
    public AttemptCounter AttemptsFor(DocumentState stage) => stage switch
    {
        DocumentState.Reserving => AttemptCounter.From(ReserveAttempts),
        DocumentState.Uploading => AttemptCounter.From(UploadAttempts),
        DocumentState.Committing => AttemptCounter.From(CommitAttempts),
        DocumentState.RemotePending => AttemptCounter.From(PollAttempts),
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Stage has no attempt counter."),
    };

    public RunDocument WithAttempts(DocumentState stage, AttemptCounter counter) => stage switch
    {
        DocumentState.Reserving => this with { ReserveAttempts = counter.DispatchedAttempts },
        DocumentState.Uploading => this with { UploadAttempts = counter.DispatchedAttempts },
        DocumentState.Committing => this with { CommitAttempts = counter.DispatchedAttempts },
        DocumentState.RemotePending => this with { PollAttempts = counter.DispatchedAttempts },
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Stage has no attempt counter."),
    };
}

/// <summary>A single durable claim pulled from SQLite and fed into the bounded processing channel.</summary>
public sealed record DocumentClaim(Guid RunId, Guid RunDocumentId, Guid CandidateId, string SourceDocumentKey);

/// <summary>
/// Pure state-transition rules for <see cref="DocumentState"/>. It owns no persistence and no I/O; the
/// engine (<c>DocumentLifecycleEngine</c>) and the store (<c>SqliteRunStore</c>) call these rules so the
/// forward chain, retry ceiling, recovery, and durable-receipt classification stay in one place.
/// </summary>
public static class DocumentLifecycle
{
private static readonly IReadOnlyList<DocumentState> ForwardChain =
[
DocumentState.Pending,
DocumentState.Snapshotting,
DocumentState.Extracting,
DocumentState.Staged,
DocumentState.Reserving,
DocumentState.Uploading,
DocumentState.Committing,
DocumentState.RemotePending,
DocumentState.Loaded,
];

/// <summary>States that have a durable receipt and are safe to preserve across a restart.</summary>
public static readonly IReadOnlyList<DocumentState> DurableSafeStages =
[
DocumentState.Pending,
DocumentState.Staged,
DocumentState.RemotePending,
DocumentState.Loaded,
DocumentState.SkippedDocumentError,
DocumentState.RetryExhaustedNetwork,
DocumentState.BlockedAuth,
DocumentState.BlockedOperatorAction,
];

/// <summary>Terminal outcomes that are never reported as a confirmed completion retry.</summary>
public static readonly IReadOnlyList<DocumentState> TerminalStates =
[
DocumentState.Loaded,
DocumentState.SkippedDocumentError,
DocumentState.RetryExhaustedNetwork,
];

public static bool CanAdvance(DocumentState state) => NextState(state) is not null;

public static DocumentState Advance(DocumentState state)
=> NextState(state) ?? throw new InvalidOperationException($"State '{state}' has no forward transition.");

public static bool CanSkip(DocumentState state)
=> state is DocumentState.Snapshotting or DocumentState.Extracting;

public static DocumentState OnDocumentFailure() => DocumentState.SkippedDocumentError;

public static bool IsRetryable(DocumentState state)
=> state is DocumentState.Reserving
or DocumentState.Uploading
or DocumentState.Committing
or DocumentState.RemotePending;

public static DocumentState OnNetworkFailure(DocumentState stage, AttemptCounter counter)
{
if (!IsRetryable(stage))
{
throw new InvalidOperationException($"Stage '{stage}' is not retryable.");
}

return counter.IsExhausted ? DocumentState.RetryExhaustedNetwork : DocumentState.RetryWait;
}

public static DocumentState ResumeFromRetry(DocumentState stage)
{
if (!IsRetryable(stage))
{
throw new InvalidOperationException($"Stage '{stage}' cannot be resumed from retry_wait.");
}

return stage;
}

public static DocumentState BlockedAuth() => DocumentState.BlockedAuth;

public static DocumentState BlockedOperatorAction() => DocumentState.BlockedOperatorAction;

public static bool IsTerminal(DocumentState state) => TerminalStates.Contains(state);

public static bool HasDurableReceipt(DocumentState state) => state switch
{
DocumentState.Pending => true,
DocumentState.Staged => true,
DocumentState.RemotePending => true,
DocumentState.Loaded => true,
DocumentState.SkippedDocumentError => true,
DocumentState.RetryExhaustedNetwork => true,
DocumentState.BlockedAuth => true,
DocumentState.BlockedOperatorAction => true,
_ => false,
};

/// <summary>
/// Returns the last durable safe stage for a document found at <paramref name="state"/> after a
/// forced kill. Transient local/remote-in-flight work returns to <see cref="DocumentState.Pending"/>;
/// durable, terminal, and intervention states are preserved.
/// </summary>
public static DocumentState Recover(DocumentState state) => state switch
{
DocumentState.Snapshotting => DocumentState.Pending,
DocumentState.Extracting => DocumentState.Pending,
DocumentState.Reserving => DocumentState.Pending,
DocumentState.Uploading => DocumentState.Pending,
DocumentState.Committing => DocumentState.Pending,
DocumentState.RetryWait => DocumentState.Pending,
DocumentState.Interrupted => DocumentState.Pending,
_ => state,
};

/// <summary>Preserves durable/terminal/intervention states; marks only in-flight work as interrupted.</summary>
public static DocumentState MarkInterrupted(DocumentState state)
=> HasDurableReceipt(state) ? state : DocumentState.Interrupted;

private static DocumentState? NextState(DocumentState state)
{
for (var index = 0; index < ForwardChain.Count; index++)
{
if (ForwardChain[index] == state)
{
return index + 1 < ForwardChain.Count ? ForwardChain[index + 1] : null;
}
}

return null;
}
}
