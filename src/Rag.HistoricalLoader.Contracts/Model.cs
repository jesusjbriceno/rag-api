using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rag.HistoricalLoader.Contracts;

// Version-1 wire model. Every field is an allowlisted payload or projection: no document content, absolute
// path, source key, credential material, configuration JSON, or raw exception text may be added here.

/// <summary>Wire serialization: snake-case field names, null omission, and the contract JSON depth bound.</summary>
public static class ControlWire
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = ControlProtocol.Limits.MaxJsonDepth,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>Request envelope: protocol version, UUID request ID, allowlisted operation, typed payload.</summary>
public sealed record ControlRequest(int ProtocolVersion, string RequestId, string Operation, JsonElement? Payload = null);

/// <summary>Response envelope: echoes version/request ID plus a status and either a typed payload or a stable error code.</summary>
public sealed record ControlResponse<T>(int ProtocolVersion, string RequestId, string Status, T? Payload = default, string? ErrorCode = null);

/// <summary>Transport limits advertised by <c>hello</c>.</summary>
public sealed record ControlLimitsPayload(int MaxFrameBytes, int MaxJsonDepth, int MaxPageSize);

/// <summary>Handshake result: supported versions, capabilities, installation and engine-instance identity, transport limits.</summary>
public sealed record HelloResult(
    IReadOnlyList<int> SupportedVersions,
    IReadOnlyList<string> Capabilities,
    string InstallationId,
    string EngineInstanceId,
    ControlLimitsPayload Limits);

/// <summary>Starts a run for an operator-approved batch. Batch resolution stays engine-side.</summary>
public sealed record StartRequest(string CommandId, string BatchId, string RunId);

/// <summary>Requests a pause. The acknowledgement is not a confirmed checkpoint.</summary>
public sealed record PauseRequest(string CommandId, string RunId);

/// <summary>Requests a resume of eligible paused or recovered work.</summary>
public sealed record ResumeRequest(string CommandId, string RunId);

/// <summary>Durable receipt for an accepted command. Contains no request payload.</summary>
public sealed record CommandReceipt(string CommandId, string Operation, string RunId, string DesiredState, string ObservedState);

/// <summary>Requests the bounded summary snapshot; no run ID is an explicit empty result.</summary>
public sealed record GetStateRequest(string? RunId = null);

/// <summary>Requests a bounded document page.</summary>
public sealed record GetDocumentsRequest(string RunId, int Limit, string? AfterDocumentId = null);

/// <summary>Requests a bounded event page from an exclusive cursor with an optional fixed upper bound.</summary>
public sealed record GetEventsRequest(int Limit, string? RunId = null, string? AfterEventId = null, string? ThroughEventId = null);

/// <summary>Persisted inventory totals only: no paths, roots, or content.</summary>
public sealed record InventoryTotals(string? ManifestId, string Completeness, int CandidateCount, long CandidateBytes);

/// <summary>Bounded coherent read of durable run/document state; every field is an allowlisted projection.</summary>
public sealed record StateSnapshot(
    string? RunId,
    string DesiredState,
    string ObservedState,
    InventoryTotals Inventory,
    IReadOnlyDictionary<string, int> DocumentCounts,
    DateTimeOffset? CheckpointAt,
    string? BlockCode,
    string EngineInstanceId,
    string? EventHighWaterMark);

/// <summary>One document page row. No source key, absolute path, extracted text, or configuration JSON.</summary>
public sealed record DocumentSummary(
    string DocumentId,
    string CandidateId,
    string State,
    IReadOnlyDictionary<string, int> Attempts,
    string? Classification = null,
    DateTimeOffset? UpdatedAt = null);

/// <summary>Individually coherent current-state page; clients refresh the summary instead of summing pages.</summary>
public sealed record DocumentPage(string RunId, IReadOnlyList<DocumentSummary> Documents, string? NextCursor);

/// <summary>One durable audit event with allowlisted fields and numeric measurements only.</summary>
public sealed record EventSummary(
    string EventId,
    DateTimeOffset Timestamp,
    string? RunId,
    string? CandidateId,
    string Action,
    string? StateTransition = null,
    string? OutcomeCode = null,
    int? Attempt = null,
    IReadOnlyDictionary<string, double>? Measurements = null);

/// <summary>Ordered audit page from an exclusive cursor, with the durable high-water mark.</summary>
public sealed record EventPage(IReadOnlyList<EventSummary> Events, string? NextCursor, string? HighWaterMark);
