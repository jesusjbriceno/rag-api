namespace Rag.HistoricalLoader.Contracts;

/// <summary>Version-1 IPC safety limits. These are not ingestion capacity claims.</summary>
public sealed record ControlLimits(int MaxFrameBytes, int MaxJsonDepth, int MaxPageSize);

/// <summary>Allowlisted operation names of the version-1 local control protocol.</summary>
public static class ControlOperations
{
    public const string Hello = "hello";
    public const string Start = "start";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string GetState = "get_state";
    public const string GetDocuments = "get_documents";
    public const string GetEvents = "get_events";

    private static readonly string[] PayloadRequired = [Start, Pause, Resume, GetDocuments, GetEvents];

    public static IReadOnlyList<string> All { get; } = [Hello, Start, Pause, Resume, GetState, GetDocuments, GetEvents];

    public static bool IsAllowed(string? operation) => operation is not null && All.Contains(operation, StringComparer.Ordinal);

    public static bool RequiresPayload(string? operation) =>
        operation is not null && PayloadRequired.Contains(operation, StringComparer.Ordinal);
}

/// <summary>Response statuses. Absence of a status is never an implicit success.</summary>
public static class ControlStatuses
{
    public const string Ok = "ok";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";

    public static IReadOnlyList<string> All { get; } = [Ok, Accepted, Rejected];

    public static bool IsKnown(string? status) => status is not null && All.Contains(status, StringComparer.Ordinal);
}

/// <summary>Stable allowlisted error codes. Raw exception text and free-form diagnostics are never wire values.</summary>
public static class ControlErrorCodes
{
    public const string UnsupportedVersion = "unsupported_version";
    public const string UnknownOperation = "unknown_operation";
    public const string MalformedRequest = "malformed_request";
    public const string CommandConflict = "command_conflict";
    public const string ResyncRequired = "resync_required";
    public const string PlatformNotSupported = "platform_not_supported";

    public static IReadOnlyList<string> All { get; } =
        [UnsupportedVersion, UnknownOperation, MalformedRequest, CommandConflict, ResyncRequired, PlatformNotSupported];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code, StringComparer.Ordinal);
}

/// <summary>Capabilities advertised by <c>hello</c>.</summary>
public static class ControlCapabilities
{
    public static IReadOnlyList<string> All { get; } = ["pause", "resume", "document_pages", "event_feed"];
}

/// <summary>Durable run states as explicit snake-case strings, never CLR enum ordinals.</summary>
public static class ControlRunStates
{
    public const string Running = "running";
    public const string PauseRequested = "pause_requested";
    public const string Pausing = "pausing";
    public const string Paused = "paused";
    public const string BlockedAuth = "blocked_auth";
    public const string BlockedOperatorAction = "blocked_operator_action";
    public const string Completed = "completed";

    public static IReadOnlyList<string> DesiredStates { get; } = [Running, PauseRequested];

    public static IReadOnlyList<string> RunStates { get; } =
        [Running, Pausing, Paused, BlockedAuth, BlockedOperatorAction, Completed];

    public static bool IsKnown(string? state) => state is not null && DesiredStates.Contains(state, StringComparer.Ordinal);
}

/// <summary>Durable per-document lifecycle states as explicit snake-case strings, never CLR enum ordinals.</summary>
public static class ControlDocumentStates
{
    public const string Pending = "pending";
    public const string Snapshotting = "snapshotting";
    public const string Extracting = "extracting";
    public const string Staged = "staged";
    public const string Reserving = "reserving";
    public const string Uploading = "uploading";
    public const string Committing = "committing";
    public const string RemotePending = "remote_pending";
    public const string Loaded = "loaded";
    public const string SkippedDocumentError = "skipped_document_error";
    public const string RetryWait = "retry_wait";
    public const string RetryExhaustedNetwork = "retry_exhausted_network";
    public const string Interrupted = "interrupted";
    public const string BlockedAuth = "blocked_auth";
    public const string BlockedOperatorAction = "blocked_operator_action";

    public static IReadOnlyList<string> All { get; } =
    [
        Pending, Snapshotting, Extracting, Staged, Reserving, Uploading, Committing, RemotePending, Loaded,
        SkippedDocumentError, RetryWait, RetryExhaustedNetwork, Interrupted, BlockedAuth, BlockedOperatorAction,
    ];

    public static bool IsKnown(string? state) => state is not null && All.Contains(state, StringComparer.Ordinal);
}

/// <summary>Protocol version and fail-closed validation performed before any dispatch.</summary>
public static class ControlProtocol
{
    public const int Version = 1;

    public static IReadOnlyList<int> SupportedVersions { get; } = [Version];

    public static ControlLimits Limits { get; } = new(MaxFrameBytes: 1_048_576, MaxJsonDepth: 16, MaxPageSize: 100);

    /// <summary>
    /// Returns a stable error code, or <c>null</c> when the request may be dispatched. The version is checked
    /// first, so an unsupported client never reaches a later branch and is never silently downgraded.
    /// </summary>
    public static string? Validate(ControlRequest? request)
    {
        if (request is null) return ControlErrorCodes.MalformedRequest;
        if (!SupportedVersions.Contains(request.ProtocolVersion)) return ControlErrorCodes.UnsupportedVersion;
        if (string.IsNullOrWhiteSpace(request.RequestId) || !Guid.TryParse(request.RequestId, out _)) return ControlErrorCodes.MalformedRequest;
        if (!ControlOperations.IsAllowed(request.Operation)) return ControlErrorCodes.UnknownOperation;
        if (ControlOperations.RequiresPayload(request.Operation) && request.Payload is null) return ControlErrorCodes.MalformedRequest;
        return null;
    }

    /// <summary>Returns a stable error code when the requested page exceeds the contract maximum, otherwise <c>null</c>.</summary>
    public static string? ValidatePageLimit(int limit) =>
        limit >= 1 && limit <= Limits.MaxPageSize ? null : ControlErrorCodes.MalformedRequest;
}
