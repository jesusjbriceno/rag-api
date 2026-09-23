using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Persistence;

namespace Rag.HistoricalLoader.Core.Lifecycle;

/// <summary>
/// The durable identity of the engine process that owns the store. A new process records a new identity;
/// refreshing it never resets runs, documents, receipts, or per-operation attempt counters.
/// </summary>
public sealed record EngineInstance(string EngineInstanceId, DateTimeOffset RecordedAt);

/// <summary>Allowlisted durable control-command operations; a durable receipt never carries free-form text.</summary>
public static class ControlCommandOperations
{
    public const string Start = "start";
    public const string Pause = "pause";
    public const string Resume = "resume";

    public static IReadOnlyList<string> All { get; } = [Start, Pause, Resume];

    public static bool IsAllowed(string? operation) => operation is not null && All.Contains(operation, StringComparer.Ordinal);
}

/// <summary>
/// Stable store-owned rejection codes. They mirror the transport vocabulary; the store↔wire agreement test is
/// a required 11.prev-c case (Core must not reference the Contracts assembly).
/// </summary>
public static class ControlStoreErrorCodes
{
    public const string CommandConflict = "command_conflict";

    public static IReadOnlyList<string> All { get; } = [CommandConflict];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code, StringComparer.Ordinal);
}

/// <summary>
/// The durable store-owned snake-case vocabulary of the control projections. It duplicates the wire
/// vocabulary in <c>Rag.HistoricalLoader.Contracts</c> on purpose: Core must not reference Contracts, and no
/// test in this slice can compare the two assemblies, so the store↔wire agreement test is a required
/// 11.prev-c case.
/// </summary>
public static class ControlStoreVocabulary
{
public const string CompletenessComplete = "complete";
public const string CompletenessIncomplete = "incomplete";
public const string CompletenessNone = "none";

/// <summary>The block code of a run blocked on authentication. No column stores it.</summary>
public const string BlockCodeAuth = "blocked_auth";

/// <summary>The block code of a run blocked on an operator action. No column stores it.</summary>
public const string BlockCodeOperatorAction = "blocked_operator_action";

/// <summary>
/// The maximum rows one bounded page may return. It mirrors the wire limit; the store↔wire agreement test is
/// a required 11.prev-c case.
/// </summary>
public const int MaxPageSize = 100;

/// <summary>
/// The four durable per-operation attempt counters a document row exposes, in wire order. The projection
/// builds exactly these keys from the durable columns, so no other counter or free-form text can travel.
/// </summary>
public static IReadOnlyList<string> AttemptKeys { get; } = ["reserve", "upload", "commit", "poll"];

public static string DocumentStateName(DocumentState state) => SnakeCase(state.ToString());

public static string DesiredStateName(RunDesiredState state) => SnakeCase(state.ToString());

public static string ObservedStateName(RunObservedState state) => SnakeCase(state.ToString());

/// <summary>
/// The safe block code derived from the durable observed state, total over the durable blocked states:
/// <see cref="BlockCodeAuth"/> when the run is blocked on authentication, <see cref="BlockCodeOperatorAction"/>
/// when it is blocked on an operator action, and <see langword="null"/> for every other observed state. No
/// state is invented and no column is added.
/// </summary>
public static string? BlockCode(RunObservedState state) => state switch
{
RunObservedState.BlockedAuth => BlockCodeAuth,
RunObservedState.BlockedOperatorAction => BlockCodeOperatorAction,
_ => null,
};

private static string SnakeCase(string name)
{
var builder = new StringBuilder(name.Length + 4);
for (var index = 0; index < name.Length; index++)
{
var character = name[index];
if (!char.IsUpper(character))
{
builder.Append(character);
continue;
}

if (index > 0)
{
builder.Append('_');
}

builder.Append(char.ToLowerInvariant(character));
}

return builder.ToString();
}
}

/// <summary>The durable store-side outcome of one command commit.</summary>
public enum CommandCommitOutcome
{
    /// <summary>The mutation, its durable receipt, and exactly one operator audit row committed together.</summary>
    Committed = 0,

    /// <summary>The command id is already committed with the same normalized fingerprint; the receipt is returned.</summary>
    Replayed = 1,

    /// <summary>The command id is already committed with a different normalized fingerprint; nothing was written.</summary>
    Conflict = 2,

    /// <summary>A store-owned or service-supplied precondition rejected the command; nothing was written.</summary>
    Rejected = 3,
}

/// <summary>
/// The typed precondition decision. It exposes no public constructor: a rejection can only be built through
/// <see cref="Reject"/>, which accepts nothing but an allowlisted <see cref="ControlStoreErrorCodes"/> value,
/// so approval/target/capacity/active-run policy stays in the service without inventing wire text.
/// </summary>
public readonly record struct PreconditionDecision
{
    private PreconditionDecision(bool accepted, string? errorCode)
    {
        Accepted = accepted;
        ErrorCode = errorCode;
    }

    /// <summary>Whether the command may proceed.</summary>
    public bool Accepted { get; }

    /// <summary>The store-owned stable code of a rejection, or <see langword="null"/> when accepted.</summary>
    public string? ErrorCode { get; }

    /// <summary>The accepting decision.</summary>
    public static PreconditionDecision Accept { get; } = new(true, null);

    /// <summary>
    /// Rejects the command with an allowlisted store-owned code. Free-form text can never become a code.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The code is not an allowlisted store error code.</exception>
    public static PreconditionDecision Reject(string errorCode)
    {
        if (!ControlStoreErrorCodes.IsKnown(errorCode))
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode), errorCode, "Not a stable store error code.");
        }

        return new(false, errorCode);
    }
}

/// <summary>
/// The durable facts the store exposes to a start precondition inside the command transaction. The
/// one-active-run and conflicting-batch rules stay the service's policy; the store only reports the facts.
/// </summary>
public sealed record StartPreconditionView(bool RunExists, Run? ActiveRun)
{
    public bool HasActiveRun => ActiveRun is not null;
}

/// <summary>The durable run the store read inside the command transaction, or <see langword="null"/> when absent.</summary>
public sealed record DesiredStatePreconditionView(Run? CurrentRun)
{
    public bool RunExists => CurrentRun is not null;
}

public delegate PreconditionDecision StartPrecondition(StartPreconditionView view);

public delegate PreconditionDecision DesiredStatePrecondition(DesiredStatePreconditionView view);

/// <summary>
/// The durable command receipt: the allowlisted fields of the <c>loader_command</c> row. The raw request is
/// never stored — only the normalized operation/IDs fingerprint it was reduced to.
/// </summary>
public sealed record ControlCommandReceipt(
    Guid CommandId, string Fingerprint, string Operation, Guid? RunId,
    RunDesiredState? DesiredState, RunObservedState? ObservedState, DateTimeOffset CreatedAt);

/// <summary>The result of one command commit. A committed or replayed outcome carries the durable receipt.</summary>
public sealed record CommandCommitResult(
    CommandCommitOutcome Outcome, string? ErrorCode = null, ControlCommandReceipt? Receipt = null, Run? Run = null);

/// <summary>
/// One document created by a start command: identity only. The durable state starts <see
/// cref="DocumentState.Pending"/> with zero per-operation attempts, so a start can never fabricate attempt
/// accounting or a terminal outcome.
/// </summary>
public sealed record StartDocument(Guid Id, Guid CandidateId, string SourceDocumentKey);

/// <summary>
/// A start command: the approved run and its selected documents plus the transport request that must never be
/// persisted. The normalized fingerprint is computed from the typed fields only.
/// </summary>
public sealed record StartCommandPlan(Guid CommandId, Run Run, IReadOnlyList<StartDocument> Documents, string? RawRequest = null);

/// <summary>A desired-state (pause/resume) intent: the run, the intent, and the observed state to expose.</summary>
public sealed record DesiredStateCommandPlan(
    Guid CommandId, Guid RunId, RunDesiredState DesiredState, RunObservedState ObservedState, string? RawRequest = null);

/// <summary>Persisted inventory totals of the latest manifest: no path, root, or document content.</summary>
public sealed record ControlInventoryTotals(string? ManifestId, string Completeness, int CandidateCount, long CandidateBytes);

/// <summary>
/// One bounded coherent read of durable control state. Every field is an allowlisted projection: the
/// configuration snapshot, source keys, absolute paths, and raw audit payloads have no field to travel in.
/// </summary>
public sealed record ControlSnapshot(
bool HasRun,
Guid? RunId,
string? DesiredState,
string? ObservedState,
ControlInventoryTotals Inventory,
IReadOnlyDictionary<string, int> DocumentCounts,
DateTimeOffset? CheckpointAt,
string? BlockCode,
string? EngineInstanceId,
long EventHighWaterMark);

/// <summary>The stable codes a bounded page read can carry. They mirror the wire vocabulary; the store↔wire
/// agreement test is a required 11.prev-c case.</summary>
public static class ControlPageErrorCodes
{
    /// <summary>The request itself is out of range; nothing was read.</summary>
    public const string MalformedRequest = "malformed_request";

    /// <summary>The cursor can no longer be served contiguously; a fresh snapshot is required.</summary>
    public const string ResyncRequired = "resync_required";

    public static IReadOnlyList<string> All { get; } = [MalformedRequest, ResyncRequired];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code, StringComparer.Ordinal);
}

/// <summary>The store-side outcome of one bounded page read.</summary>
public enum ControlPageOutcome
{
    /// <summary>The page was produced; an empty page is a valid answer, never an error.</summary>
    Ok = 0,

    /// <summary>The request is out of range; no page was read and no cursor was consumed.</summary>
    InvalidRequest = 1,

    /// <summary>The cursor is no longer valid (retention floor or restore); the caller must resynchronise.</summary>
    ResyncRequired = 2,
}

/// <summary>
/// One bounded page read: the page, or the store-owned reason it could not be produced. There is no public
/// constructor, so only an allowlisted <see cref="ControlPageErrorCodes"/> value can travel with a rejection.
/// </summary>
public sealed record ControlPageResult<T>
{
    private ControlPageResult(ControlPageOutcome outcome, T? page, string? errorCode)
    {
        Outcome = outcome;
        Page = page;
        ErrorCode = errorCode;
    }

    public ControlPageOutcome Outcome { get; }

    /// <summary>The page, or <see langword="null"/> unless the outcome is <see cref="ControlPageOutcome.Ok"/>.</summary>
    public T? Page { get; }

    public string? ErrorCode { get; }

    public static ControlPageResult<T> Completed(T page) => new(ControlPageOutcome.Ok, page, null);

    public static ControlPageResult<T> InvalidRequest() =>
        new(ControlPageOutcome.InvalidRequest, default, ControlPageErrorCodes.MalformedRequest);

    public static ControlPageResult<T> ResyncRequired() =>
        new(ControlPageOutcome.ResyncRequired, default, ControlPageErrorCodes.ResyncRequired);
}

/// <summary>
/// A durable audit payload carried a value that cannot cross the allowlisted page boundary as a numeric
/// measurement. The read fails closed: the raw text is never coerced, dropped, or forwarded.
/// </summary>
public sealed class ControlMeasurementBoundaryException : Exception
{
    public ControlMeasurementBoundaryException(long eventId)
        : base("A durable audit measurement is not a numeric measurement and was rejected at the page boundary.")
        => EventId = eventId;

    /// <summary>The durable event whose measurement was rejected. It carries no payload text.</summary>
    public long EventId { get; }
}

/// <summary>
/// The typed keyset position of a document page over <c>(created_at, run_document_id)</c>. The opaque wire
/// encoding of this position belongs to 11.prev-d; the store exposes the typed position itself.
/// </summary>
public sealed record DocumentPageCursor(DateTimeOffset CreatedAt, Guid RunDocumentId);

/// <summary>One current-state document row: no source key, path, extracted text, hash, remote ID, or configuration.</summary>
public sealed record DocumentPageRow(
    Guid DocumentId,
    Guid CandidateId,
    string State,
    IReadOnlyDictionary<string, int> Attempts,
    string? Classification,
    DateTimeOffset UpdatedAt);

/// <summary>
/// One individually coherent current-state page of a run. A state change committed between two pages is
/// reported by the later page, so pages are refreshed rather than summed as an immutable corpus snapshot.
/// </summary>
public sealed record DocumentPage(Guid RunId, IReadOnlyList<DocumentPageRow> Documents, DocumentPageCursor? NextCursor);

/// <summary>
/// One durable audit event on the page boundary: an ascending durable cursor, UTC time, run/candidate identity,
/// allowlisted text fields, the attempt number, and the raw numeric measurement or nothing at all.
/// </summary>
public sealed record EventPageRow(
    long EventId,
    DateTimeOffset Timestamp,
    Guid? RunId,
    Guid? CandidateId,
    string Action,
    string? StateTransition,
    string? OutcomeCode,
    int AttemptNumber,
    double? Measurements);

/// <summary>
/// One ascending page of durable events plus the durable high-water mark. <see cref="NextCursor"/> is the
/// exclusive cursor of the next page, and is never set when the page did not fill its limit or already reached
/// the effective bound.
/// </summary>
public sealed record EventPage(IReadOnlyList<EventPageRow> Events, long? NextCursor, long HighWaterMark);

/// <summary>
/// Store-owned control state outside the run/document lifecycle surface: the durable engine-instance record,
/// the atomic command composites (start and desired-state intents), the durable receipts they commit, the
/// operator audit row each command writes, and the bounded coherent control snapshot.
/// </summary>
public sealed class SqliteControlStore
{
    private const string SelectEngineInstance =
        "SELECT engine_instance_id, recorded_at FROM loader_engine_instance LIMIT 1;";

    private const string DeleteEngineInstances = "DELETE FROM loader_engine_instance;";

    private const string InsertEngineInstance =
        "INSERT INTO loader_engine_instance (engine_instance_id, recorded_at) VALUES ($id, $recorded);";

    private const string SelectCommand =
        "SELECT command_id, normalized_fingerprint, operation, run_id, desired_state, observed_state, created_at FROM loader_command WHERE command_id = $id;";

    private const string SelectRunColumns =
        "run_id, sample_id, collection_id, desired_state, observed_state, engine_version, configuration_snapshot, started_at, ended_at, checkpoint_at";

    private const string SelectMostRecentRun =
        $"SELECT {SelectRunColumns} FROM run ORDER BY started_at DESC, run_id DESC LIMIT 1;";

    private const string SelectLatestManifest =
        "SELECT manifest_id, state FROM manifest ORDER BY start_time DESC, manifest_id DESC LIMIT 1;";

    private const string SelectCandidateTotals =
        "SELECT COUNT(*), COALESCE(SUM(byte_size), 0) FROM candidate WHERE manifest_id = $id;";

    private const string SelectDocumentCounts =
        "SELECT state, COUNT(*) FROM run_document WHERE run_id = $id GROUP BY state;";

    private const string SelectEventHighWater = "SELECT COALESCE(MAX(event_id), 0) FROM audit_event;";

    private const string SelectMinRetainedEvent = "SELECT COALESCE(MIN(event_id), 0) FROM audit_event;";

    private const string SelectDocumentPage = """
        SELECT run_document_id, candidate_id, state,
               reserve_attempts, upload_attempts, commit_attempts, poll_attempts,
               terminal_classification, created_at, updated_at
        FROM run_document
        WHERE run_id = $run
          AND ($afterCreated IS NULL
               OR created_at > $afterCreated
               OR (created_at = $afterCreated AND run_document_id > $afterId))
        ORDER BY created_at ASC, run_document_id ASC
        LIMIT $limit;
        """;

    private const string SelectEventPage = """
        SELECT event_id, utc_timestamp, run_id, candidate_id, action, state_transition,
               outcome_code, attempt_number, measurements
        FROM audit_event
        WHERE event_id > $after
          AND event_id <= $ceiling
          AND ($run IS NULL OR run_id = $run)
        ORDER BY event_id ASC
        LIMIT $limit;
        """;

    private const string InsertCommandSql =
        "INSERT INTO loader_command (command_id, normalized_fingerprint, operation, run_id, desired_state, observed_state, created_at) VALUES ($id, $fingerprint, $operation, $run, $desired, $observed, $created);";

    private const string InsertAuditSql =
        "INSERT INTO audit_event (utc_timestamp, run_id, candidate_id, action, state_transition, outcome_code, attempt_number, measurements) VALUES ($ts, $run, NULL, $action, $transition, NULL, 0, NULL);";

    private const string InsertDocumentSql =
        "INSERT INTO run_document (run_document_id, run_id, candidate_id, source_document_key, state, created_at, updated_at) VALUES ($id, $run, $candidate, $source, $pending, $created, $created);";

    private const string InsertRunSql =
        "INSERT INTO run (run_id, sample_id, collection_id, desired_state, observed_state, engine_version, configuration_snapshot, started_at, ended_at, checkpoint_at) VALUES ($id, $sample, $collection, $desired, $observed, $version, $config, $started, $ended, $checkpoint);";

    private const string UpdateDesiredStateSql =
        "UPDATE run SET desired_state = $desired, observed_state = $observed WHERE run_id = $id;";

    private readonly SqliteStore _store;

    public SqliteControlStore(SqliteStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Creates or refreshes the single durable engine-instance identity through the store's single writer. The
    /// refresh is one transaction that replaces the identity row; it never appends history and never touches
    /// run, run_document, or audit_event rows.
    /// </summary>
    public async Task RecordEngineInstanceAsync(string engineInstanceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(engineInstanceId))
        {
            throw new ArgumentException("An engine-instance id is required.", nameof(engineInstanceId));
        }

        var recordedAt = DateTimeOffset.UtcNow;

        await _store.RunInTransactionAsync(
            async (connection, transaction, ct) =>
            {
                await ExecuteAsync(connection, transaction, DeleteEngineInstances, ct);
                await ExecuteAsync(connection, transaction, InsertEngineInstance, ct,
                    ("$id", engineInstanceId), ("$recorded", recordedAt.UtcTicks));
            },
            cancellationToken);
    }

    /// <summary>Reads the durable engine-instance identity, or <see langword="null"/> when none was recorded.</summary>
    public async Task<EngineInstance?> GetEngineInstanceAsync(CancellationToken cancellationToken = default)
        => await _store.RunExclusiveAsync(
            async (connection, ct) =>
            {
                await using var command = CreateCommand(connection, null, SelectEngineInstance);
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    return (EngineInstance?)null;
                }

                return new EngineInstance(reader.GetString(0), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero));
            },
            cancellationToken);

    /// <summary>Reads the durable receipt for one command id, or <see langword="null"/> when nothing was committed.</summary>
    public async Task<ControlCommandReceipt?> GetCommandReceiptAsync(Guid commandId, CancellationToken cancellationToken = default)
        => await _store.RunExclusiveAsync(
            async (connection, ct) => await ReadCommandAsync(connection, null, commandId, ct),
            cancellationToken);

        /// <summary>
        /// Reads the bounded control snapshot in one coherent read transaction: the selected run — the most recent
        /// run when <paramref name="runId"/> is null — with its counts by durable state, checkpoint time and
        /// derived block code, the engine identity, the candidate-derived inventory projection of the latest
        /// manifest, and the durable event high-water mark. A missing run is an explicit empty result and never
        /// suppresses the inventory projection.
        /// </summary>
            public async Task<ControlSnapshot> GetControlSnapshotAsync(Guid? runId = null, CancellationToken cancellationToken = default)
            {
                ControlSnapshot? snapshot = null;

                await _store.RunInTransactionAsync(
                async (connection, transaction, ct) =>
                {
                    var run = runId is { } id
                        ? await ReadRunAsync(connection, transaction, id, ct)
                        : await ReadSingleRunAsync(connection, transaction, SelectMostRecentRun, ct);

                    snapshot = new ControlSnapshot(
                        run is not null,
                        run?.Id,
                        run is null ? null : ControlStoreVocabulary.DesiredStateName(run.DesiredState),
                        run is null ? null : ControlStoreVocabulary.ObservedStateName(run.ObservedState),
                        await ReadInventoryTotalsAsync(connection, transaction, ct),
                        run is null
                            ? (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(StringComparer.Ordinal)
                            : await ReadDocumentCountsAsync(connection, transaction, run.Id, ct),
                        run?.CheckpointAt,
                        run is null ? null : ControlStoreVocabulary.BlockCode(run.ObservedState),
                        await ReadEngineInstanceIdAsync(connection, transaction, ct),
                        await ReadEventHighWaterMarkAsync(connection, transaction, ct));
                },
                cancellationToken);

            return snapshot ?? throw new InvalidOperationException("The snapshot read produced no result.");
        }

    /// <summary>
    /// Reads one bounded, individually coherent current-state page of a run, ordered by the durable keyset
    /// <c>(created_at, run_document_id)</c>. <paramref name="after"/> is the typed position the previous page
    /// ended at, so traversal neither duplicates nor skips a row while another writer moves states, and a row whose
    /// state changed between two pages is reported by the later page. A run without documents is an empty page.
    /// </summary>
    public Task<ControlPageResult<DocumentPage>> GetDocumentPageAsync(
        Guid runId,
        int limit,
        DocumentPageCursor? after = null,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || limit > ControlStoreVocabulary.MaxPageSize)
        {
            return Task.FromResult(ControlPageResult<DocumentPage>.InvalidRequest());
        }

        return _store.RunExclusiveAsync(
            async (connection, ct) =>
            {
                await using var command = CreateCommand(
                    connection, null, SelectDocumentPage,
                    ("$run", runId.ToString()),
                    ("$afterCreated", after is null ? (object)DBNull.Value : after.CreatedAt.UtcTicks),
                    ("$afterId", after is null ? (object)DBNull.Value : after.RunDocumentId.ToString()),
                    ("$limit", limit));

                await using var reader = await command.ExecuteReaderAsync(ct);
                var documents = new List<DocumentPageRow>(limit);
                DocumentPageCursor? nextCursor = null;
                while (await reader.ReadAsync(ct))
                {
                    var createdAt = new DateTimeOffset(reader.GetInt64(8), TimeSpan.Zero);
                    var documentId = Guid.Parse(reader.GetString(0));
                    documents.Add(new DocumentPageRow(
                        documentId,
                        Guid.Parse(reader.GetString(1)),
                        ControlStoreVocabulary.DocumentStateName((DocumentState)reader.GetInt64(2)),
                        new Dictionary<string, int>(StringComparer.Ordinal)
                        {
                            [ControlStoreVocabulary.AttemptKeys[0]] = (int)reader.GetInt64(3),
                            [ControlStoreVocabulary.AttemptKeys[1]] = (int)reader.GetInt64(4),
                            [ControlStoreVocabulary.AttemptKeys[2]] = (int)reader.GetInt64(5),
                            [ControlStoreVocabulary.AttemptKeys[3]] = (int)reader.GetInt64(6),
                        },
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        new DateTimeOffset(reader.GetInt64(9), TimeSpan.Zero)));

                    nextCursor = new DocumentPageCursor(createdAt, documentId);
                }

                // A page that filled its limit may still have followers: it hands out its own keyset position. A short
                // page is the end of the traversal and offers no cursor, so no read loops forever on a partial page.
                var page = new DocumentPage(runId, documents, documents.Count == limit ? nextCursor : null);
                return ControlPageResult<DocumentPage>.Completed(page);
            },
            cancellationToken);
    }

    /// <summary>
    /// Reads one bounded, ascending page of durable events from an exclusive cursor. The cursor validity check is
    /// total and gap-safe: a cursor above the durable high-water mark (restore or rollback) or below the derived
    /// retention floor (pruning) yields <see cref="ControlPageOutcome.ResyncRequired"/> instead of silently
    /// skipping an audit gap, while the exact boundary position still reads contiguously.
    /// </summary>
    public async Task<ControlPageResult<EventPage>> GetEventPageAsync(
        int limit,
        long afterEventId = 0,
        long? throughEventId = null,
        Guid? runId = null,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || limit > ControlStoreVocabulary.MaxPageSize || afterEventId < 0)
        {
            return ControlPageResult<EventPage>.InvalidRequest();
        }

        ControlPageResult<EventPage>? result = null;

        await _store.RunInTransactionAsync(
            async (connection, transaction, ct) =>
            {
                var highWaterMark = await ReadEventHighWaterMarkAsync(connection, transaction, ct);
                if (throughEventId is { } through && (through < afterEventId || through > highWaterMark))
                {
                    result = ControlPageResult<EventPage>.InvalidRequest();
                    return;
                }

                // A cursor above the durable mark names an event that no longer exists: a restore or rollback dropped
                // the newest rows after the client acknowledged them, so the gap is unfillable and the traversal must
                // resync. The mark itself is still contiguous (an empty page), never a resync.
                if (afterEventId > highWaterMark)
                {
                    result = ControlPageResult<EventPage>.ResyncRequired();
                    return;
                }

                // Retention: a client whose next unread event was pruned has an unfillable gap. The floor itself and
                // its immediate predecessor are contiguous: only a cursor below floor - 1 has actually lost a row.
                var minRetainedEventId = await ReadMinRetainedEventIdAsync(connection, transaction, ct);
                if (afterEventId < minRetainedEventId - 1)
                {
                    result = ControlPageResult<EventPage>.ResyncRequired();
                    return;
                }

                var ceiling = throughEventId ?? highWaterMark;
                var events = await ReadEventRowsAsync(connection, transaction, afterEventId, ceiling, limit, runId, ct);

                // Only a full page that has not already reached its effective bound offers a next cursor; an empty or
                // short page, or one that ends exactly on the bound, is the explicit end of the traversal.
                var nextCursor = events.Count == limit && events[^1].EventId < ceiling ? events[^1].EventId : (long?)null;
                result = ControlPageResult<EventPage>.Completed(new EventPage(events, nextCursor, highWaterMark));
            },
            cancellationToken);

        return result ?? throw new InvalidOperationException("The event-page read produced no result.");
    }

    /// <summary>
    /// Commits a start command: the run row, its documents, exactly one operator audit row, and the durable
    /// receipt in one transaction through the single writer. Repeating the same command id with the same
    /// normalized fingerprint replays that receipt; a changed fingerprint conflicts; a rejected precondition
    /// writes nothing. A duplicate row in the plan aborts the whole transaction (the plan is never
    /// pre-validated, so the acknowledgement boundary stays honestly testable).
    /// </summary>
    public async Task<CommandCommitResult> CommitStartAsync(
        StartCommandPlan plan,
        StartPrecondition? precondition = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Run);
        ArgumentNullException.ThrowIfNull(plan.Documents);
        if (plan.CommandId == Guid.Empty)
        {
            throw new ArgumentException("A command id is required.", nameof(plan));
        }

        var fingerprint = Fingerprint(ControlCommandOperations.Start, plan.Run.Id, plan.Run.CollectionId, plan.Documents);
        CommandCommitResult? result = null;

        await _store.RunInTransactionAsync(
            async (connection, transaction, ct) =>
            {
                var settled = ReplayOrConflict(await ReadCommandAsync(connection, transaction, plan.CommandId, ct), fingerprint);
                if (settled is not null)
                {
                    result = settled;
                    return;
                }

                var decision = precondition?.Invoke(new StartPreconditionView(
                    await RunExistsAsync(connection, transaction, plan.Run.Id, ct),
                    await ReadActiveRunAsync(connection, transaction, ct))) ?? PreconditionDecision.Accept;
                if (!decision.Accepted)
                {
                    result = new CommandCommitResult(CommandCommitOutcome.Rejected, ResolveRejectionCode(decision));
                    return;
                }

                var recordedAt = DateTimeOffset.UtcNow;
                await ExecuteAsync(connection, transaction, InsertRunSql, ct,
                    ("$id", plan.Run.Id.ToString()), ("$sample", plan.Run.SampleId?.ToString() ?? (object)DBNull.Value),
                    ("$collection", plan.Run.CollectionId), ("$desired", (int)plan.Run.DesiredState),
                    ("$observed", (int)plan.Run.ObservedState), ("$version", plan.Run.EngineVersion),
                    ("$config", plan.Run.ConfigurationSnapshot), ("$started", plan.Run.StartedAt.UtcTicks),
                    ("$ended", plan.Run.EndedAt?.UtcTicks ?? (object)DBNull.Value),
                    ("$checkpoint", plan.Run.CheckpointAt?.UtcTicks ?? (object)DBNull.Value));

                foreach (var document in plan.Documents)
                {
                    // A plain insert, never an upsert: a duplicate or conflicting row must abort everything.
                    await ExecuteAsync(connection, transaction, InsertDocumentSql, ct,
                        ("$id", document.Id.ToString()), ("$run", plan.Run.Id.ToString()),
                        ("$candidate", document.CandidateId.ToString()), ("$source", document.SourceDocumentKey),
                        ("$pending", (int)DocumentState.Pending), ("$created", recordedAt.UtcTicks));
                }

                await InsertAuditAsync(connection, transaction, recordedAt, plan.Run.Id,
                    OperatorAction(ControlCommandOperations.Start), null, ct);

                var receipt = new ControlCommandReceipt(
                    plan.CommandId, fingerprint, ControlCommandOperations.Start, plan.Run.Id,
                    plan.Run.DesiredState, plan.Run.ObservedState, recordedAt);
                await InsertCommandAsync(connection, transaction, receipt, ct);

                result = new CommandCommitResult(CommandCommitOutcome.Committed, Receipt: receipt, Run: plan.Run);
            },
            cancellationToken);

        return result ?? throw new InvalidOperationException("The command transaction produced no result.");
    }

    /// <summary>
    /// Commits a desired-state (pause/resume) intent: the run's desired and observed state, exactly one
    /// operator audit row carrying the state transition, and the durable receipt in one transaction. The
    /// precondition view is read inside that transaction, so a stale run/batch view cannot win the race. An
    /// unknown run id is rejected without writing anything.
    /// </summary>
    public async Task<CommandCommitResult> CommitDesiredStateAsync(
        DesiredStateCommandPlan plan,
        DesiredStatePrecondition? precondition = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.CommandId == Guid.Empty)
        {
            throw new ArgumentException("A command id is required.", nameof(plan));
        }

        var operation = plan.DesiredState == RunDesiredState.PauseRequested
            ? ControlCommandOperations.Pause
            : ControlCommandOperations.Resume;
        var fingerprint = Fingerprint(operation, plan.RunId, null, []);
        CommandCommitResult? result = null;

        await _store.RunInTransactionAsync(
            async (connection, transaction, ct) =>
            {
                var settled = ReplayOrConflict(await ReadCommandAsync(connection, transaction, plan.CommandId, ct), fingerprint);
                if (settled is not null)
                {
                    result = settled;
                    return;
                }

                var current = await ReadRunAsync(connection, transaction, plan.RunId, ct);
                var decision = precondition?.Invoke(new DesiredStatePreconditionView(current)) ?? PreconditionDecision.Accept;
                if (!decision.Accepted)
                {
                    result = new CommandCommitResult(CommandCommitOutcome.Rejected, ResolveRejectionCode(decision));
                    return;
                }

                if (current is null)
                {
                    // The store never invents a run: an unknown run id is a stable rejection, not a new run.
                    result = new CommandCommitResult(CommandCommitOutcome.Rejected, ControlStoreErrorCodes.CommandConflict);
                    return;
                }

                var recordedAt = DateTimeOffset.UtcNow;
                var transition = $"{ControlStoreVocabulary.ObservedStateName(current.ObservedState)}->{ControlStoreVocabulary.ObservedStateName(plan.ObservedState)}";

                await ExecuteAsync(connection, transaction, UpdateDesiredStateSql, ct,
                    ("$desired", (int)plan.DesiredState), ("$observed", (int)plan.ObservedState), ("$id", plan.RunId.ToString()));
                await InsertAuditAsync(connection, transaction, recordedAt, plan.RunId, OperatorAction(operation), transition, ct);

                var receipt = new ControlCommandReceipt(
                    plan.CommandId, fingerprint, operation, plan.RunId,
                    plan.DesiredState, plan.ObservedState, recordedAt);
                await InsertCommandAsync(connection, transaction, receipt, ct);

                result = new CommandCommitResult(
                    CommandCommitOutcome.Committed,
                    Receipt: receipt,
                    Run: current with { DesiredState = plan.DesiredState, ObservedState = plan.ObservedState });
            },
            cancellationToken);

        return result ?? throw new InvalidOperationException("The command transaction produced no result.");
    }

        private static CommandCommitResult Conflict()
            => new(CommandCommitOutcome.Conflict, ControlStoreErrorCodes.CommandConflict);

        /// <summary>
        /// Resolves the store-owned code of a rejection, refusing anything outside the allowlist. The decision type
        /// has no public constructor, so this only catches a malformed decision — an unknown code, or a rejection
        /// carrying no code at all, such as the struct's zero value — and fails closed before anything is written.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The decision carries no allowlisted store error code.</exception>
        private static string ResolveRejectionCode(PreconditionDecision decision)
            => decision.ErrorCode is { } code && ControlStoreErrorCodes.IsKnown(code)
                ? code
                : throw new ArgumentOutOfRangeException(
                    nameof(decision), decision.ErrorCode, "Not a stable store error code.");

    /// <summary>
    /// Returns <c>null</c> while the command id is unused; otherwise the durable receipt of an identical
    /// command, or a conflict for a command id whose normalized fingerprint changed.
    /// </summary>
    private static CommandCommitResult? ReplayOrConflict(ControlCommandReceipt? existing, string fingerprint)
        => existing is null
            ? null
            : existing.Fingerprint == fingerprint
                ? new CommandCommitResult(CommandCommitOutcome.Replayed, Receipt: existing)
                : Conflict();

    private static string OperatorAction(string operation) => operation switch
    {
        ControlCommandOperations.Start => "start_requested",
        ControlCommandOperations.Pause => "pause_requested",
        ControlCommandOperations.Resume => "resume_requested",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not an allowlisted command operation."),
    };

    /// <summary>
    /// The normalized fingerprint of a command: its allowlisted operation plus its normalized IDs, hashed so no
    /// raw request text, path, source key, or token can survive into a durable column.
    /// </summary>
    private static string Fingerprint(string operation, Guid runId, string? collectionId, IReadOnlyList<StartDocument> documents)
    {
        var builder = new StringBuilder(operation).Append('|').Append(runId);
        if (collectionId is not null)
        {
            builder.Append('|').Append(collectionId);
        }

        foreach (var identity in documents
                     .Select(document => $"{document.Id}:{document.CandidateId}")
                     .OrderBy(identity => identity, StringComparer.Ordinal))
        {
            builder.Append('|').Append(identity);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

        private static async Task<ControlCommandReceipt?> ReadCommandAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid commandId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, SelectCommand, ("$id", commandId.ToString()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapCommand(reader) : null;
    }

    private static ControlCommandReceipt MapCommand(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
        reader.IsDBNull(4) ? null : (RunDesiredState)reader.GetInt64(4),
        reader.IsDBNull(5) ? null : (RunObservedState)reader.GetInt64(5),
        new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero));

    /// <summary>
    /// The candidate-derived inventory totals of the latest manifest. An incomplete scan is never reported as
    /// complete, and an older completed scan never masks a newer running one.
    /// </summary>
    private static async Task<ControlInventoryTotals> ReadInventoryTotalsAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        Guid manifestId;
        string completeness;

        await using (var command = CreateCommand(connection, transaction, SelectLatestManifest))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                // No manifest at all: an empty inventory is 'none', never a completed scan.
                return new ControlInventoryTotals(null, ControlStoreVocabulary.CompletenessNone, 0, 0L);
            }

            manifestId = Guid.Parse(reader.GetString(0));
            completeness = (ManifestState)reader.GetInt64(1) == ManifestState.Complete
                ? ControlStoreVocabulary.CompletenessComplete
                : ControlStoreVocabulary.CompletenessIncomplete;
        }

        await using var totals = CreateCommand(connection, transaction, SelectCandidateTotals, ("$id", manifestId.ToString()));
        await using var totalsReader = await totals.ExecuteReaderAsync(cancellationToken);
        await totalsReader.ReadAsync(cancellationToken);

        return new ControlInventoryTotals(manifestId.ToString(), completeness, (int)totalsReader.GetInt64(0), totalsReader.GetInt64(1));
    }

    private static async Task<IReadOnlyDictionary<string, int>> ReadDocumentCountsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, SelectDocumentCounts, ("$id", runId.ToString()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts[ControlStoreVocabulary.DocumentStateName((DocumentState)reader.GetInt64(0))] = (int)reader.GetInt64(1);
        }

        return counts;
    }

    private static async Task<string?> ReadEngineInstanceIdAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, SelectEngineInstance);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? reader.GetString(0) : null;
    }

    private static async Task<long> ReadEventHighWaterMarkAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, SelectEventHighWater);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<long> ReadMinRetainedEventIdAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, SelectMinRetainedEvent);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Reads the ascending event window after an exclusive cursor up to a fixed ceiling, optionally filtered to one
    /// run. Every returned measurement is numeric-or-null; a durable payload that is not fails closed.
    /// </summary>
    private static async Task<IReadOnlyList<EventPageRow>> ReadEventRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long afterEventId,
        long ceiling,
        int limit,
        Guid? runId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection, transaction, SelectEventPage,
            ("$after", afterEventId),
            ("$ceiling", ceiling),
            ("$run", runId?.ToString() ?? (object)DBNull.Value),
            ("$limit", limit));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<EventPageRow>(limit);
        while (await reader.ReadAsync(cancellationToken))
        {
            var eventId = reader.GetInt64(0);
            events.Add(new EventPageRow(
                eventId,
                new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                (int)reader.GetInt64(7),
                ReadMeasurements(reader, 8, eventId)));
        }

        return events;
    }

    /// <summary>
    /// Maps one durable measurement to the page boundary: <see langword="null"/> for no measurement, a finite
    /// number for a numeric one, and a fail-closed rejection for anything else (raw JSON, a non-finite literal, or
    /// any other text). The raw payload is never coerced, dropped, or forwarded.
    /// </summary>
    /// <exception cref="ControlMeasurementBoundaryException">The durable value is not a numeric measurement.</exception>
    private static double? ReadMeasurements(SqliteDataReader reader, int ordinal, long eventId)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var text = reader.GetString(ordinal);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value)
            || double.IsInfinity(value))
        {
            throw new ControlMeasurementBoundaryException(eventId);
        }

        return value;
    }

    private static async Task<bool> RunExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection, transaction, "SELECT COUNT(*) FROM run WHERE run_id = $id;", ("$id", runId.ToString()));
        return (long)(await command.ExecuteScalarAsync(cancellationToken))! > 0L;
    }

    private static async Task<Run?> ReadRunAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid runId, CancellationToken cancellationToken)
        => await ReadSingleRunAsync(
            connection, transaction, $"SELECT {SelectRunColumns} FROM run WHERE run_id = $id;", cancellationToken,
            ("$id", runId.ToString()));

    private static async Task<Run?> ReadActiveRunAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
        => await ReadSingleRunAsync(
            connection,
            transaction,
            $"SELECT {SelectRunColumns} FROM run WHERE observed_state IN ($running, $pausing) ORDER BY started_at, run_id LIMIT 1;",
            cancellationToken,
            ("$running", (int)RunObservedState.Running),
            ("$pausing", (int)RunObservedState.Pausing));

    private static async Task<Run?> ReadSingleRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapRun(reader) : null;
    }

    private static Run MapRun(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)), reader.GetString(2),
        (RunDesiredState)reader.GetInt64(3), (RunObservedState)reader.GetInt64(4), reader.GetString(5), reader.GetString(6),
        new DateTimeOffset(reader.GetInt64(7), TimeSpan.Zero),
        reader.IsDBNull(8) ? null : new DateTimeOffset(reader.GetInt64(8), TimeSpan.Zero),
        reader.IsDBNull(9) ? null : new DateTimeOffset(reader.GetInt64(9), TimeSpan.Zero));

    private static async Task<int> InsertAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset recordedAt,
        Guid? runId,
        string action,
        string? transition,
        CancellationToken cancellationToken)
        => await ExecuteAsync(connection, transaction, InsertAuditSql, cancellationToken,
            ("$ts", recordedAt.UtcTicks), ("$run", runId?.ToString() ?? (object)DBNull.Value),
            ("$action", action), ("$transition", transition ?? (object)DBNull.Value));

    private static async Task<int> InsertCommandAsync(
        SqliteConnection connection, SqliteTransaction transaction, ControlCommandReceipt receipt, CancellationToken cancellationToken)
        => await ExecuteAsync(connection, transaction, InsertCommandSql, cancellationToken,
            ("$id", receipt.CommandId.ToString()), ("$fingerprint", receipt.Fingerprint), ("$operation", receipt.Operation),
            ("$run", receipt.RunId?.ToString() ?? (object)DBNull.Value),
            ("$desired", receipt.DesiredState is null ? (object)DBNull.Value : (int)receipt.DesiredState.Value),
            ("$observed", receipt.ObservedState is null ? (object)DBNull.Value : (int)receipt.ObservedState.Value),
            ("$created", receipt.CreatedAt.UtcTicks));

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
