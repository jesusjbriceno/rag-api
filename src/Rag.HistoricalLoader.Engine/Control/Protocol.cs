using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Rag.HistoricalLoader.Contracts;

namespace Rag.HistoricalLoader.Engine.Control;

/// <summary>
/// Explicit version-1 IPC safety defaults of the local control loop. The frame/depth/page bounds are the
/// contract's own limits (one source of truth); the deadlines and connection/request bounds are this
/// implementation's recorded defaults. They are IPC safety limits, never ingestion capacity claims.
/// </summary>
public sealed record ControlTransportLimits(
    int MaxFrameBytes,
    int MaxJsonDepth,
    int MaxPageSize,
    TimeSpan ReadDeadline,
    TimeSpan WriteDeadline,
    int MaxConcurrentConnections,
    int MaxRequestsPerConnection,
    int ReadChunkBytes)
{
    /// <summary>The recorded defaults: 1 MiB frames, depth 16, 100-row pages, 10 s deadlines, 4 connections.</summary>
    public static ControlTransportLimits Default { get; } = new(
        ControlProtocol.Limits.MaxFrameBytes,
        ControlProtocol.Limits.MaxJsonDepth,
        ControlProtocol.Limits.MaxPageSize,
        ReadDeadline: TimeSpan.FromSeconds(10),
        WriteDeadline: TimeSpan.FromSeconds(10),
        MaxConcurrentConnections: 4,
        MaxRequestsPerConnection: 4096,
        ReadChunkBytes: 16 * 1024);
}

/// <summary>The outcome of reading one frame. An absence of a frame is never an implicit dispatch.</summary>
public enum ControlFrameReadStatus
{
    /// <summary>One complete length-prefixed frame was read.</summary>
    Frame = 0,

    /// <summary>The peer closed the stream cleanly before a length prefix arrived.</summary>
    EndOfStream = 1,

    /// <summary>The frame failed closed and must never reach dispatch.</summary>
    Rejected = 2,
}

/// <summary>
/// The typed reason a frame failed closed. It is an internal diagnosis, never a wire value: every rejection
/// is reported to a client through the allowlisted <see cref="ControlErrorCodes"/> vocabulary.
/// </summary>
public enum ControlFrameRejection
{
    /// <summary>No rejection; a complete frame was read.</summary>
    None = 0,

    /// <summary>The declared frame length was zero, so there is no JSON document to dispatch.</summary>
    EmptyFrame = 1,

    /// <summary>The stream ended inside the four-byte length prefix.</summary>
    LengthPrefixTruncated = 2,

    /// <summary>The declared length exceeds the frame limit and was rejected before any payload allocation.</summary>
    DeclaredLengthTooLarge = 3,

    /// <summary>The stream ended before the declared payload length was delivered.</summary>
    PayloadTruncated = 4,

    /// <summary>The payload is not exactly one well-formed JSON document.</summary>
    JsonMalformed = 5,

    /// <summary>The payload nests more containers than the contract depth limit.</summary>
    JsonDepthExceeded = 6,

    /// <summary>The payload is not valid UTF-8; invalid bytes are never silently replaced.</summary>
    InvalidUtf8 = 7,

    /// <summary>The frame did not arrive inside the bounded read deadline.</summary>
    ReadDeadlineExceeded = 8,
}

/// <summary>The outcome of writing one frame.</summary>
public enum ControlFrameWriteStatus
{
    /// <summary>The length prefix and the payload were written.</summary>
    Written = 0,

    /// <summary>The payload exceeds the frame limit and nothing was written.</summary>
    TooLarge = 1,

    /// <summary>The write did not complete inside the bounded write deadline.</summary>
    DeadlineExceeded = 2,
}

/// <summary>
/// One frame read result: the decoded JSON, a clean end of stream, or a typed rejection. The wire-facing
/// <see cref="ErrorCode"/> is always an allowlisted constant, never raw exception text.
/// </summary>
public sealed record ControlFrameReadResult(
    ControlFrameReadStatus Status,
    string? Json,
    ControlFrameRejection Rejection,
    int DeclaredLength)
{
    /// <summary>The stable wire code of a rejected frame, or <see langword="null"/> when nothing was rejected.</summary>
    public string? ErrorCode =>
        Status == ControlFrameReadStatus.Rejected ? ControlErrorCodes.MalformedRequest : null;

    /// <summary>One complete frame carrying its decoded UTF-8 JSON payload.</summary>
    public static ControlFrameReadResult Frame(string json, int declaredLength) =>
        new(ControlFrameReadStatus.Frame, json, ControlFrameRejection.None, declaredLength);

    /// <summary>A clean end of stream: the peer closed between frames.</summary>
    public static ControlFrameReadResult EndOfStream() =>
        new(ControlFrameReadStatus.EndOfStream, null, ControlFrameRejection.None, 0);

    /// <summary>A fail-closed rejection that must never reach dispatch.</summary>
    public static ControlFrameReadResult Rejected(ControlFrameRejection rejection, int declaredLength = 0) =>
        new(ControlFrameReadStatus.Rejected, null, rejection, declaredLength);
}

/// <summary>
/// Version-1 byte-mode frame codec: a four-byte unsigned little-endian byte length followed by UTF-8 JSON.
/// It rejects an over-limit frame before allocating or reading any payload byte, reads a valid payload in
/// bounded chunks, and never uses newline framing, binary object serialization, or arbitrary type names.
/// </summary>
public static class ControlFrameCodec
{
    /// <summary>The size of the unsigned little-endian length prefix that starts every frame.</summary>
    public const int LengthPrefixBytes = 4;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Reads one frame from <paramref name="stream"/>. A clean close between frames returns
    /// <see cref="ControlFrameReadStatus.EndOfStream"/>; any truncated, empty, or over-limit frame returns a
    /// rejection whose <see cref="ControlFrameReadResult.ErrorCode"/> is a stable allowlisted code.
    /// </summary>
    public static async ValueTask<ControlFrameReadResult> ReadAsync(
        Stream stream,
        ControlTransportLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var effective = limits ?? ControlTransportLimits.Default;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(effective.ReadDeadline);
        try
        {
            return await ReadCoreAsync(stream, effective, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The frame deadline expired, not the caller's token: report it as a fail-closed rejection.
            return ControlFrameReadResult.Rejected(ControlFrameRejection.ReadDeadlineExceeded);
        }
    }

    private static async ValueTask<ControlFrameReadResult> ReadCoreAsync(
        Stream stream,
        ControlTransportLimits effective,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[LengthPrefixBytes];
        var prefixRead = await ReadAtMostAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        if (prefixRead == 0)
        {
            return ControlFrameReadResult.EndOfStream();
        }

        if (prefixRead < LengthPrefixBytes)
        {
            return ControlFrameReadResult.Rejected(ControlFrameRejection.LengthPrefixTruncated);
        }

        // Compare in unsigned space: a declared length near uint.MaxValue must fail closed instead of
        // wrapping into a negative array size.
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (declared == 0)
        {
            return ControlFrameReadResult.Rejected(ControlFrameRejection.EmptyFrame);
        }

        if (declared > (uint)effective.MaxFrameBytes)
        {
            // Rejected before allocation: no payload buffer exists yet and no payload byte was requested.
            // The reported length saturates so a near-uint.MaxValue declaration cannot wrap negative.
            return ControlFrameReadResult.Rejected(
                ControlFrameRejection.DeclaredLengthTooLarge,
                (int)Math.Min(declared, (uint)int.MaxValue));
        }

        var declaredLength = (int)declared;
        var payload = new byte[declaredLength];
        var filled = 0;
        while (filled < declaredLength)
        {
            var chunk = Math.Min(effective.ReadChunkBytes, declaredLength - filled);
            var read = await stream.ReadAsync(payload.AsMemory(filled, chunk), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return ControlFrameReadResult.Rejected(ControlFrameRejection.PayloadTruncated, declaredLength);
            }

            filled += read;
        }

        var payloadText = DecodeStrictUtf8(payload);
        if (payloadText is null)
        {
            return ControlFrameReadResult.Rejected(ControlFrameRejection.InvalidUtf8, declaredLength);
        }

        var rejection = ValidateJson(payloadText, effective.MaxJsonDepth);
        return rejection == ControlFrameRejection.None
            ? ControlFrameReadResult.Frame(payloadText, declaredLength)
            : ControlFrameReadResult.Rejected(rejection, declaredLength);
    }

    /// <summary>Decodes UTF-8 with no replacement fallback: invalid bytes mean the frame fails closed.</summary>
    private static string? DecodeStrictUtf8(byte[] payload)
    {
        try
        {
            return StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Validates that the payload is exactly one well-formed JSON document within the depth limit. Depth is
    /// the number of nested containers, so depth 16 is the deepest accepted document and depth 17 fails
    /// closed. Comments, trailing commas, and a second top-level value are all rejected.
    /// </summary>
    private static ControlFrameRejection ValidateJson(string json, int maxDepth)
    {
        var depth = 0;
        var rootValues = 0;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = maxDepth + 2,
            });

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        if (depth == 0)
                        {
                            rootValues++;
                        }

                        depth++;
                        if (depth > maxDepth)
                        {
                            return ControlFrameRejection.JsonDepthExceeded;
                        }

                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        depth--;
                        break;
                    default:
                        if (depth == 0)
                        {
                            rootValues++;
                        }

                        break;
                }
            }
        }
        catch (JsonException)
        {
            return ControlFrameRejection.JsonMalformed;
        }

        return depth == 0 && rootValues == 1
            ? ControlFrameRejection.None
            : ControlFrameRejection.JsonMalformed;
    }

    /// <summary>
    /// Writes one frame: the unsigned little-endian byte length, then the UTF-8 JSON payload. A payload above
    /// the frame limit is refused before anything is written; a write that does not complete inside the
    /// bounded write deadline reports <see cref="ControlFrameWriteStatus.DeadlineExceeded"/>.
    /// </summary>
    public static async ValueTask<ControlFrameWriteStatus> WriteAsync(
        Stream stream,
        string json,
        ControlTransportLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(json);
        var effective = limits ?? ControlTransportLimits.Default;

        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > effective.MaxFrameBytes)
        {
            return ControlFrameWriteStatus.TooLarge;
        }

        var prefix = new byte[LengthPrefixBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(effective.WriteDeadline);
        try
        {
            await stream.WriteAsync(prefix, deadline.Token).ConfigureAwait(false);
            await stream.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
            return ControlFrameWriteStatus.Written;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ControlFrameWriteStatus.DeadlineExceeded;
        }
    }

    private static async ValueTask<int> ReadAtMostAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = await stream.ReadAsync(destination[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
