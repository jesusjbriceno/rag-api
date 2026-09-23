using System.Globalization;
using Rag.HistoricalLoader.Contracts;
using Rag.HistoricalLoader.Core.Lifecycle;
using CoreDocumentPage = Rag.HistoricalLoader.Core.Lifecycle.DocumentPage;
using CoreDocumentRow = Rag.HistoricalLoader.Core.Lifecycle.DocumentPageRow;
using CoreEventPage = Rag.HistoricalLoader.Core.Lifecycle.EventPage;
using CoreEventRow = Rag.HistoricalLoader.Core.Lifecycle.EventPageRow;

namespace Rag.HistoricalLoader.Engine.Control;

/// <summary>
/// The one place where durable rows become wire payloads. Every projection names its fields explicitly, so a
/// durable column can never reach the wire by accident: source keys, absolute paths, configuration snapshots,
/// hashes, remote identifiers, and raw audit payloads have no field to travel in. The only numeric measurement
/// that may cross the boundary is the allowlisted <c>staged_bytes</c> count.
/// </summary>
public static class ControlProjections
{
    /// <summary>The single allowlisted audit measurement key.</summary>
    public const string StagedBytesMeasurement = "staged_bytes";

    /// <summary>Projects a durable command receipt: allowlisted ids and states only, never the request.</summary>
    public static CommandReceipt Receipt(ControlCommandReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new CommandReceipt(
            receipt.CommandId.ToString(),
            receipt.Operation,
            receipt.RunId?.ToString() ?? string.Empty,
            receipt.DesiredState is { } desired ? DesiredStateName(desired) : string.Empty,
            receipt.ObservedState is { } observed ? ObservedStateName(observed) : string.Empty);
    }

    /// <summary>
    /// Projects the bounded control snapshot. The durable engine-instance identity is presented from the store
    /// when it is recorded and from the engine's own recorded identity otherwise; an unrecorded instance is
    /// never presented as an invented empty identifier.
    /// </summary>
    public static StateSnapshot State(ControlSnapshot snapshot, string engineInstanceId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new StateSnapshot(
            snapshot.RunId?.ToString(),
            snapshot.HasRun ? snapshot.DesiredState ?? string.Empty : AbsentState,
            snapshot.HasRun ? snapshot.ObservedState ?? string.Empty : AbsentState,
            new InventoryTotals(
                snapshot.Inventory.ManifestId,
                snapshot.Inventory.Completeness,
                snapshot.Inventory.CandidateCount,
                snapshot.Inventory.CandidateBytes),
            snapshot.DocumentCounts,
            snapshot.CheckpointAt,
            snapshot.BlockCode,
            snapshot.EngineInstanceId ?? engineInstanceId,
            snapshot.EventHighWaterMark.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Projects one bounded document page with its opaque keyset cursor.</summary>
    public static Contracts.DocumentPage Documents(CoreDocumentPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new Contracts.DocumentPage(
            page.RunId.ToString(),
            page.Documents.Select(Document).ToArray(),
            page.NextCursor is { } cursor ? ControlDocumentCursor.Encode(cursor) : null);
    }

    /// <summary>Projects one bounded event page with its exclusive cursor and the durable high-water mark.</summary>
    public static Contracts.EventPage Events(CoreEventPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new Contracts.EventPage(
            page.Events.Select(Event).ToArray(),
            page.NextCursor?.ToString(CultureInfo.InvariantCulture),
            page.HighWaterMark.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Projects one document row: identity, state, per-operation attempts, classification, timestamp.</summary>
    public static DocumentSummary Document(CoreDocumentRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new DocumentSummary(
            row.DocumentId.ToString(),
            row.CandidateId.ToString(),
            row.State,
            row.Attempts,
            row.Classification,
            row.UpdatedAt);
    }

    /// <summary>
    /// Projects one durable audit event. <see cref="EventSummary.Measurements"/> is populated for the
    /// allowlisted staged-byte action only: every other durable numeric payload is dropped rather than
    /// forwarded as a measurement the contract never promised.
    /// </summary>
    public static EventSummary Event(CoreEventRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new EventSummary(
            row.EventId.ToString(CultureInfo.InvariantCulture),
            row.Timestamp,
            row.RunId?.ToString(),
            row.CandidateId?.ToString(),
            row.Action,
            row.StateTransition,
            row.OutcomeCode,
            row.AttemptNumber,
            Measurements(row));
    }

    /// <summary>The wire name of a durable desired state. Unknown values fail closed instead of reaching the wire.</summary>
    public static string DesiredStateName(RunDesiredState state) => state switch
    {
        RunDesiredState.Running => ControlRunStates.Running,
        RunDesiredState.PauseRequested => ControlRunStates.PauseRequested,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a durable desired state."),
    };

    /// <summary>The wire name of a durable observed state. Unknown values fail closed instead of reaching the wire.</summary>
    public static string ObservedStateName(RunObservedState state) => state switch
    {
        RunObservedState.Running => ControlRunStates.Running,
        RunObservedState.Pausing => ControlRunStates.Pausing,
        RunObservedState.Paused => ControlRunStates.Paused,
        RunObservedState.BlockedAuth => ControlRunStates.BlockedAuth,
        RunObservedState.BlockedOperatorAction => ControlRunStates.BlockedOperatorAction,
        RunObservedState.Completed => ControlRunStates.Completed,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a durable observed state."),
    };

    /// <summary>
    /// The wire presentation of a run-less snapshot. The contract has no "absent" field, so the two state
    /// fields present the empty string while <c>run_id</c> being absent is the explicit empty result.
    /// </summary>
    private const string AbsentState = "";

    private static IReadOnlyDictionary<string, double>? Measurements(CoreEventRow row)
        => string.Equals(row.Action, StagedBytesMeasurement, StringComparison.Ordinal) && row.Measurements is { } value
            ? new Dictionary<string, double>(StringComparer.Ordinal) { [StagedBytesMeasurement] = value }
            : null;
}
