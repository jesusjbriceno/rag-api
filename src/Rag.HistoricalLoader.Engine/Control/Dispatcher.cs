using System.Text.Json;
using Rag.HistoricalLoader.Contracts;

namespace Rag.HistoricalLoader.Engine.Control;

/// <summary>
/// The delegation seam of the local control protocol. The dispatcher validates a request and hands it to
/// this handler, which owns the engine-side work. No path, pipe, database, credential, or ingestion-policy
/// parameter crosses this seam: the handler resolves every engine-side fact itself.
/// </summary>
public interface IControlCommandHandler
{
    /// <summary>Handles one already-validated request. This method is never called for a rejected request.</summary>
    ValueTask<ControlDispatchOutcome> HandleAsync(ControlRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// A handler reply: an allowlisted status with a typed contract payload or a stable error code. A free-form
/// diagnostic cannot be constructed, so raw exception text can never become a wire value.
/// </summary>
public sealed record ControlDispatchOutcome
{
    private ControlDispatchOutcome(string status, object? payload, string? errorCode)
    {
        Status = status;
        Payload = payload;
        ErrorCode = errorCode;
    }

    /// <summary>One of the allowlisted <see cref="ControlStatuses"/> values.</summary>
    public string Status { get; }

    /// <summary>A typed contract DTO, or <see langword="null"/>.</summary>
    public object? Payload { get; }

    /// <summary>The stable error code of a rejection, or <see langword="null"/>.</summary>
    public string? ErrorCode { get; }

    /// <summary>A successful reply with an optional typed payload.</summary>
    public static ControlDispatchOutcome Ok(object? payload = null) => new(ControlStatuses.Ok, payload, null);

    /// <summary>An accepted command whose durable effect is not yet confirmed.</summary>
    public static ControlDispatchOutcome Accepted(object? payload = null) => new(ControlStatuses.Accepted, payload, null);

    /// <summary>
    /// Rejects the request with an allowlisted code.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The code is not a stable control error code.</exception>
    public static ControlDispatchOutcome Rejected(string errorCode)
    {
        if (!ControlErrorCodes.IsKnown(errorCode))
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode), errorCode, "Not a stable control error code.");
        }

        return new ControlDispatchOutcome(ControlStatuses.Rejected, null, errorCode);
    }
}

/// <summary>The outcome of one dispatcher turn.</summary>
public enum ControlDispatchStatus
{
    /// <summary>The request was validated and delegated to the handler.</summary>
    Handled = 0,

    /// <summary>Nothing was delegated; <see cref="ControlDispatchResult.ResponseJson"/> carries a rejection.</summary>
    Rejected = 1,

    /// <summary>The peer closed the stream between frames; no response exists.</summary>
    EndOfStream = 2,
}

/// <summary>
/// One dispatcher turn: the status, the echoable request ID, the stable error code when rejected, whether the
/// transport must close the connection, and the exact response frame JSON.
/// </summary>
public sealed record ControlDispatchResult(
    ControlDispatchStatus Status,
    string? RequestId,
    string? ErrorCode,
    bool CloseConnection,
    string? ResponseJson)
{
    /// <summary>A clean end of stream: no dispatch and no response frame.</summary>
    public static ControlDispatchResult EndOfStream() =>
        new(ControlDispatchStatus.EndOfStream, null, null, false, null);
}

/// <summary>One bounded connection slot with its per-connection request accounting.</summary>
public sealed class ControlConnection
{
    private int _requestCount;

    internal ControlConnection(int index) => Index = index;

    /// <summary>An opaque local slot index, used only for diagnostics inside this process.</summary>
    public int Index { get; }

    /// <summary>How many frames this connection has processed.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    internal bool Closed { get; set; }

    internal void RecordRequest() => Interlocked.Increment(ref _requestCount);
}

/// <summary>
/// The transport-agnostic control dispatcher: it reads one length-prefixed frame from an abstract stream,
/// validates it against the version-1 contract, delegates at most once, and writes exactly one response
/// frame. It owns no ingestion policy, no host wiring, and no pipe or database path.
/// </summary>
public sealed class ControlDispatcher
{
    private readonly IControlCommandHandler _handler;
    private int _activeConnections;
    private int _nextConnectionIndex;

    public ControlDispatcher(IControlCommandHandler handler, ControlTransportLimits? limits = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        Limits = limits ?? ControlTransportLimits.Default;
    }

    /// <summary>The recorded deadline, frame, page, connection, and request limits.</summary>
    public ControlTransportLimits Limits { get; }

    /// <summary>How many connection slots are currently open.</summary>
    public int ActiveConnectionCount => Volatile.Read(ref _activeConnections);

    /// <summary>Opens one connection slot, or returns <see langword="null"/> when the bound is saturated.</summary>
    public ControlConnection? TryOpenConnection()
    {
        while (true)
        {
            var current = Volatile.Read(ref _activeConnections);
            if (current >= Limits.MaxConcurrentConnections)
            {
                return null;
            }

            if (Interlocked.CompareExchange(ref _activeConnections, current + 1, current) == current)
            {
                return new ControlConnection(Interlocked.Increment(ref _nextConnectionIndex) - 1);
            }
        }
    }

    /// <summary>Releases a connection slot. A second close of the same slot is a no-op.</summary>
    public bool CloseConnection(ControlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Closed)
        {
            return false;
        }

        connection.Closed = true;
        Interlocked.Decrement(ref _activeConnections);
        return true;
    }

    /// <summary>
    /// Validates and delegates one request without touching a transport. A rejected request is never
    /// delegated, and the returned <see cref="ControlDispatchResult.ResponseJson"/> is the exact response
    /// envelope a client receives.
    /// </summary>
    public async Task<ControlDispatchResult> DispatchAsync(ControlRequest? request, CancellationToken cancellationToken = default)
    {
        var rejection = ControlProtocol.Validate(request) ?? ValidatePageRequest(request!);
        if (rejection is not null)
        {
            // The frame boundary is intact, so the connection stays usable for the next frame.
            return Reject(request?.RequestId, rejection, closeConnection: false);
        }

        var outcome = await _handler.HandleAsync(request!, cancellationToken).ConfigureAwait(false);
        var response = new ControlResponse<object?>(
            ControlProtocol.Version,
            request!.RequestId,
            outcome.Status,
            outcome.Payload,
            outcome.ErrorCode);
        return new ControlDispatchResult(
            ControlDispatchStatus.Handled,
            request.RequestId,
            outcome.ErrorCode,
            CloseConnection: false,
            ControlWire.Serialize(response));
    }

    /// <summary>
    /// Reads one frame from <paramref name="stream"/>, validates it, delegates at most once, and writes
    /// exactly one response frame. A frame that fails closed writes a stable rejection envelope and marks the
    /// connection for closing, because its framing can no longer be trusted.
    /// </summary>
    public async Task<ControlDispatchResult> ProcessFrameAsync(
        ControlConnection connection,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(stream);

        if (connection.RequestCount >= Limits.MaxRequestsPerConnection)
        {
            // The request bound is exhausted: the peer must reconnect. Nothing is read and nothing is
            // delegated, so a saturated connection can never reach ingestion work.
            var saturated = Reject(null, ControlErrorCodes.MalformedRequest, closeConnection: true);
            return await WriteResponseAsync(stream, saturated, cancellationToken).ConfigureAwait(false);
        }

        connection.RecordRequest();

        var frame = await ControlFrameCodec.ReadAsync(stream, Limits, cancellationToken).ConfigureAwait(false);
        if (frame.Status == ControlFrameReadStatus.EndOfStream)
        {
            return ControlDispatchResult.EndOfStream();
        }

        if (frame.Status == ControlFrameReadStatus.Rejected)
        {
            var rejected = Reject(null, frame.ErrorCode ?? ControlErrorCodes.MalformedRequest, closeConnection: true);
            return await WriteResponseAsync(stream, rejected, cancellationToken).ConfigureAwait(false);
        }

        var request = BindRequest(frame.Json);
        var result = request is null
            ? Reject(null, ControlErrorCodes.MalformedRequest, closeConnection: false)
            : await DispatchAsync(request, cancellationToken).ConfigureAwait(false);

        return await WriteResponseAsync(stream, result, cancellationToken).ConfigureAwait(false);
    }

    private static string? ValidatePageRequest(ControlRequest request) => request.Operation switch
    {
        ControlOperations.GetDocuments => ValidatePageLimit(Bind<GetDocumentsRequest>(request.Payload)?.Limit),
        ControlOperations.GetEvents => ValidatePageLimit(Bind<GetEventsRequest>(request.Payload)?.Limit),
        _ => null,
    };

    private static string? ValidatePageLimit(int? limit) =>
        limit is null ? ControlErrorCodes.MalformedRequest : ControlProtocol.ValidatePageLimit(limit.Value);

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

    private static ControlRequest? BindRequest(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return ControlWire.Deserialize<ControlRequest>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ControlDispatchResult Reject(string? requestId, string errorCode, bool closeConnection)
    {
        var response = new ControlResponse<object?>(
            ControlProtocol.Version,
            requestId ?? string.Empty,
            ControlStatuses.Rejected,
            Payload: null,
            ErrorCode: errorCode);
        return new ControlDispatchResult(
            ControlDispatchStatus.Rejected,
            requestId,
            errorCode,
            closeConnection,
            ControlWire.Serialize(response));
    }

    private async Task<ControlDispatchResult> WriteResponseAsync(
        Stream stream,
        ControlDispatchResult result,
        CancellationToken cancellationToken)
    {
        var write = await ControlFrameCodec.WriteAsync(stream, result.ResponseJson!, Limits, cancellationToken).ConfigureAwait(false);
        return write == ControlFrameWriteStatus.Written ? result : result with { CloseConnection = true };
    }
}
