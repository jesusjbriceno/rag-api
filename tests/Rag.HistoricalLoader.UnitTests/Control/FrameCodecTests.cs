using System.Buffers.Binary;
using System.Text;
using Rag.HistoricalLoader.Contracts;
using Rag.HistoricalLoader.Engine.Control;

namespace Rag.HistoricalLoader.UnitTests.Control;

/// <summary>
/// Version-1 byte-mode frame codec: a four-byte unsigned little-endian length prefix followed by UTF-8
/// JSON, the 1 MiB frame limit, and fail-closed rejection that happens before any oversized payload is
/// allocated or read. The codec is transport-agnostic: every case here runs against an instrumented
/// in-memory stream, so no pipe, ACL, or Windows surface is involved.
/// </summary>
public sealed class FrameCodecTests
{
    private const string RequestId = "6f1d2c3b-4a59-4d8e-9f01-2a3b4c5d6e7f";

    private static string RequestJson(string operation = ControlOperations.GetState) =>
        ControlWire.Serialize(new ControlRequest(ControlProtocol.Version, RequestId, operation));

    private static byte[] LengthPrefix(int length)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)length);
        return prefix;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var buffer = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(buffer, offset);
            offset += part.Length;
        }

        return buffer;
    }

    /// <summary>A JSON frame whose UTF-8 byte length is exactly the 1 MiB frame limit.</summary>
    private static string AtLimitJson()
    {
        var head = "{\"protocol_version\":1,\"request_id\":\"" + RequestId + "\",\"operation\":\"get_state\",\"pad\":\"";
        var tail = "\"}";
        return head + new string('a', ControlProtocol.Limits.MaxFrameBytes - head.Length - tail.Length) + tail;
    }

    [Fact]
    public async Task RoundTrip_WritesLittleEndianLengthPrefix_ThenUtf8JsonPayload()
    {
        var json = RequestJson();
        var stream = new InstrumentedStream([]);

        var written = await ControlFrameCodec.WriteAsync(stream, json);

        Assert.Equal(ControlFrameWriteStatus.Written, written);
        var bytes = stream.WrittenToArray();
        Assert.Equal(json, Encoding.UTF8.GetString(bytes, 4, bytes.Length - 4));
        Assert.Equal((uint)Encoding.UTF8.GetByteCount(json), BinaryPrimitives.ReadUInt32LittleEndian(bytes));

        var read = await ControlFrameCodec.ReadAsync(new InstrumentedStream(bytes));
        Assert.Equal(ControlFrameReadStatus.Frame, read.Status);
        Assert.Equal(ControlFrameRejection.None, read.Rejection);
        Assert.Null(read.ErrorCode);
        Assert.Equal(json, read.Json);
        Assert.Equal(bytes.Length - 4, read.DeclaredLength);
    }

    [Fact]
    public async Task LengthPrefix_CountsUtf8Bytes_NotCharacters()
    {
        // "Añ" is three UTF-8 bytes but two UTF-16 characters; the emoji is four bytes and two chars.
        var json = "{\"protocol_version\":1,\"request_id\":\"" + RequestId + "\",\"operation\":\"get_state\",\"pad\":\"Añ😀\"}";
        var utf8 = Encoding.UTF8.GetBytes(json);
        Assert.NotEqual(json.Length, utf8.Length);

        var stream = new InstrumentedStream([]);
        await ControlFrameCodec.WriteAsync(stream, json);

        var bytes = stream.WrittenToArray();
        Assert.Equal((uint)utf8.Length, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(utf8, bytes[4..]);

        var read = await ControlFrameCodec.ReadAsync(new InstrumentedStream(bytes));
        Assert.Equal(ControlFrameReadStatus.Frame, read.Status);
        Assert.Equal(json, read.Json);
    }

    [Fact]
    public async Task EmptyStream_IsACleanEndOfStream_AndNeverRequestsAPayload()
    {
        var stream = new InstrumentedStream([]);

        var read = await ControlFrameCodec.ReadAsync(stream);

        Assert.Equal(ControlFrameReadStatus.EndOfStream, read.Status);
        Assert.Equal(ControlFrameRejection.None, read.Rejection);
        Assert.Null(read.ErrorCode);
        Assert.Null(read.Json);
        Assert.Equal(4, stream.MaxRequestedBufferBytes);
        Assert.Equal(0, stream.TotalBytesRead);
    }

    [Fact]
    public async Task ZeroLengthFrame_IsRejectedAsAnEmptyFrame()
    {
        var stream = new InstrumentedStream(LengthPrefix(0));

        var read = await ControlFrameCodec.ReadAsync(stream);

        Assert.Equal(ControlFrameReadStatus.Rejected, read.Status);
        Assert.Equal(ControlFrameRejection.EmptyFrame, read.Rejection);
        Assert.Equal(ControlErrorCodes.MalformedRequest, read.ErrorCode);
        Assert.Null(read.Json);
        Assert.Equal(4, stream.MaxRequestedBufferBytes);
    }

    [Fact]
    public async Task PartialLengthPrefix_IsRejectedWithoutReadingAPayload()
    {
        var stream = new InstrumentedStream([0x2a, 0x00]);

        var read = await ControlFrameCodec.ReadAsync(stream);

        Assert.Equal(ControlFrameReadStatus.Rejected, read.Status);
        Assert.Equal(ControlFrameRejection.LengthPrefixTruncated, read.Rejection);
        Assert.Equal(ControlErrorCodes.MalformedRequest, read.ErrorCode);
        Assert.Equal(2, stream.TotalBytesRead);
    }

    [Fact]
    public async Task DeclaredLengthBeyondTheFrameLimit_IsRejectedBeforeAnyPayloadReadOrAllocation()
    {
        var declared = ControlProtocol.Limits.MaxFrameBytes + 1;
        var stream = new InstrumentedStream(LengthPrefix(declared));

        var read = await ControlFrameCodec.ReadAsync(stream);

        Assert.Equal(ControlFrameReadStatus.Rejected, read.Status);
        Assert.Equal(ControlFrameRejection.DeclaredLengthTooLarge, read.Rejection);
        Assert.Equal(ControlErrorCodes.MalformedRequest, read.ErrorCode);
        Assert.Null(read.Json);
        Assert.Equal(declared, read.DeclaredLength);

        // The proof that the rejection happens before allocation: the codec never asked the stream for a
        // payload byte and never requested a buffer larger than the four-byte length prefix.
        Assert.Equal(4, stream.TotalBytesRead);
        Assert.Equal(4, stream.MaxRequestedBufferBytes);
        Assert.Equal(0, stream.PayloadBytesRead);
    }

    [Fact]
    public async Task PayloadShorterThanItsDeclaredLength_IsRejectedAsATruncatedFrame()
    {
        var stream = new InstrumentedStream(Concat(LengthPrefix(64), Encoding.UTF8.GetBytes(RequestJson())[..10]));

        var read = await ControlFrameCodec.ReadAsync(stream);

        Assert.Equal(ControlFrameReadStatus.Rejected, read.Status);
        Assert.Equal(ControlFrameRejection.PayloadTruncated, read.Rejection);
        Assert.Equal(ControlErrorCodes.MalformedRequest, read.ErrorCode);
        Assert.Null(read.Json);
        Assert.Equal(64, read.DeclaredLength);
    }

    [Fact]
    public async Task FrameExactlyAtTheLimit_IsAccepted_AndReadInBoundedChunks()
    {
        var json = AtLimitJson();
        Assert.Equal(ControlProtocol.Limits.MaxFrameBytes, Encoding.UTF8.GetByteCount(json));

        var stream = new InstrumentedStream(Concat(LengthPrefix(Encoding.UTF8.GetByteCount(json)), Encoding.UTF8.GetBytes(json)));

        var read = await ControlFrameCodec.ReadAsync(stream);

        Assert.Equal(ControlFrameReadStatus.Frame, read.Status);
        Assert.Equal(json, read.Json);
        Assert.Equal(ControlProtocol.Limits.MaxFrameBytes, read.DeclaredLength);
        Assert.Equal(ControlProtocol.Limits.MaxFrameBytes, stream.PayloadBytesRead);
        Assert.True(
            stream.MaxRequestedBufferBytes <= ControlTransportLimits.Default.ReadChunkBytes,
            $"the codec requested {stream.MaxRequestedBufferBytes} bytes in one read, above the chunk bound");
    }

    /// <summary>Builds a JSON value nested <paramref name="levels"/> containers deep around a scalar.</summary>
    private static string Nest(int levels)
    {
        var body = "1";
        for (var level = 0; level < levels; level++)
        {
            body = "{\"a\":" + body + "}";
        }

        return body;
    }

    /// <summary>A request envelope whose total JSON depth is <paramref name="payloadLevels"/> plus the envelope itself.</summary>
    private static string RequestJsonWithPayloadNested(int payloadLevels) =>
        "{\"protocol_version\":1,\"request_id\":\"" + RequestId + "\",\"operation\":\"get_state\",\"payload\":" + Nest(payloadLevels) + "}";

    private static byte[] Framed(string json) => Concat(LengthPrefix(Encoding.UTF8.GetByteCount(json)), Encoding.UTF8.GetBytes(json));

    [Fact]
    public async Task MalformedJson_FailsClosedBeforeDispatch()
    {
        const string json = "{\"protocol_version\":1,\"request_id\":";

        var read = await ControlFrameCodec.ReadAsync(new InstrumentedStream(Framed(json)));

        Assert.Equal(ControlFrameReadStatus.Rejected, read.Status);
        Assert.Equal(ControlFrameRejection.JsonMalformed, read.Rejection);
        Assert.Equal(ControlErrorCodes.MalformedRequest, read.ErrorCode);
        Assert.Null(read.Json);
    }

    [Theory]
    [InlineData("{\"a\":1 /* comment */}")]
    [InlineData("{\"a\":1,}")]
    [InlineData("{\"a\":1} {\"b\":2}")]
    [InlineData("   ")]
    [InlineData("{\"a\":1")]
    [InlineData("not json at all")]
    public async Task DocumentsThatAreNotExactlyOneWellFormedJsonValue_FailClosedAsMalformed(string json)
    {
        var read = await ControlFrameCodec.ReadAsync(new InstrumentedStream(Framed(json)));

        Assert.Equal(ControlFrameReadStatus.Rejected, read.Status);
        Assert.Equal(ControlFrameRejection.JsonMalformed, read.Rejection);
        Assert.Equal(ControlErrorCodes.MalformedRequest, read.ErrorCode);
    }

    [Fact]
    public async Task JsonDeeperThanTheDepthLimit_FailsClosedBeforeDispatch()
    {
        // 16 payload containers plus the envelope is depth 17, one above the contract limit.
        var json = RequestJsonWithPayloadNested(ControlProtocol.Limits.MaxJsonDepth);

        var read = await ControlFrameCodec.ReadAsync(new InstrumentedStream(Framed(json)));

        Assert.Equal(ControlFrameReadStatus.Rejected, read.Status);
        Assert.Equal(ControlFrameRejection.JsonDepthExceeded, read.Rejection);
        Assert.Equal(ControlErrorCodes.MalformedRequest, read.ErrorCode);
        Assert.Null(read.Json);
    }

    [Fact]
    public async Task JsonExactlyAtTheDepthLimit_IsAccepted_AndTheContractAcceptsItToo()
    {
        // 15 payload containers plus the envelope is exactly depth 16.
        var json = RequestJsonWithPayloadNested(ControlProtocol.Limits.MaxJsonDepth - 1);

        var read = await ControlFrameCodec.ReadAsync(new InstrumentedStream(Framed(json)));

        Assert.Equal(ControlFrameReadStatus.Frame, read.Status);
        Assert.Equal(json, read.Json);

        // The codec bound and the wire serializer bound are the same limit: what the codec admits, the
        // contract deserializer must also admit.
        Assert.NotNull(ControlWire.Deserialize<ControlRequest>(json));
    }

    [Fact]
    public async Task MultiByteUtf8SplicedByTheDeclaredLength_FailsClosed()
    {
        var json = "{\"protocol_version\":1,\"request_id\":\"" + RequestId + "\",\"operation\":\"get_state\",\"pad\":\"ñ\"}";
        var utf8 = Encoding.UTF8.GetBytes(json);
        var spliced = utf8[..^3];

        // The payload really does end inside a multi-byte sequence.
        Assert.Equal(0xC3, spliced[^1]);
        Assert.Throws<DecoderFallbackException>(() => new UTF8Encoding(false, true).GetString(spliced));

        var read = await ControlFrameCodec.ReadAsync(new InstrumentedStream(Concat(LengthPrefix(spliced.Length), spliced)));

        Assert.Equal(ControlFrameReadStatus.Rejected, read.Status);
        Assert.Equal(ControlFrameRejection.InvalidUtf8, read.Rejection);
        Assert.Equal(ControlErrorCodes.MalformedRequest, read.ErrorCode);
        Assert.Null(read.Json);
    }

    [Fact]
    public async Task EscapedNewlinesStayInsideOneFrame_AndConsecutiveFramesAreReadInOrder()
    {
        var first = "{\"protocol_version\":1,\"request_id\":\"" + RequestId + "\",\"operation\":\"get_state\",\"pad\":\"line\\nbreak\"}";
        var second = RequestJson(ControlOperations.Hello);
        var stream = new InstrumentedStream(Concat(Framed(first), Framed(second)));

        var firstRead = await ControlFrameCodec.ReadAsync(stream);
        var secondRead = await ControlFrameCodec.ReadAsync(stream);
        var end = await ControlFrameCodec.ReadAsync(stream);

        // A newline inside the payload is data, not a frame boundary: the frame is length-prefixed only.
        Assert.Equal(ControlFrameReadStatus.Frame, firstRead.Status);
        Assert.Equal(first, firstRead.Json);
        Assert.Contains("\\n", firstRead.Json);
        Assert.Equal(ControlFrameReadStatus.Frame, secondRead.Status);
        Assert.Equal(second, secondRead.Json);
        Assert.Equal(ControlFrameReadStatus.EndOfStream, end.Status);
    }

    [Fact]
    public async Task ReadDeadlineExpiry_FailsClosedWithAStableCode()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new InstrumentedStream([], gate);
        var limits = ControlTransportLimits.Default with { ReadDeadline = TimeSpan.FromMilliseconds(50) };

        var read = await ControlFrameCodec.ReadAsync(stream, limits);

        Assert.Equal(ControlFrameReadStatus.Rejected, read.Status);
        Assert.Equal(ControlFrameRejection.ReadDeadlineExceeded, read.Rejection);
        Assert.Equal(ControlErrorCodes.MalformedRequest, read.ErrorCode);
        Assert.Null(read.Json);
    }

    [Fact]
    public async Task CallerCancellationDuringRead_PropagatesInsteadOfBeingReportedAsARejection()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new InstrumentedStream([], gate);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ControlFrameCodec.ReadAsync(stream, ControlTransportLimits.Default, cancellation.Token));
    }

    [Fact]
    public async Task WriteRejectsAnOverLimitPayload_WithoutWritingAnything()
    {
        var stream = new InstrumentedStream([]);
        var json = AtLimitJson() + " ";
        Assert.True(Encoding.UTF8.GetByteCount(json) > ControlProtocol.Limits.MaxFrameBytes);

        var status = await ControlFrameCodec.WriteAsync(stream, json);

        Assert.Equal(ControlFrameWriteStatus.TooLarge, status);
        Assert.Empty(stream.WrittenToArray());
    }

    [Fact]
    public async Task WriteDeadlineExpiry_IsReportedInsteadOfBlocking()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new InstrumentedStream([], gate);
        var limits = ControlTransportLimits.Default with { WriteDeadline = TimeSpan.FromMilliseconds(50) };

        var status = await ControlFrameCodec.WriteAsync(stream, RequestJson(), limits);

        Assert.Equal(ControlFrameWriteStatus.DeadlineExceeded, status);
        Assert.Empty(stream.WrittenToArray());
    }

    [Fact]
    public async Task CallerCancellationDuringWrite_PropagatesInsteadOfBeingReportedAsAStatus()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new InstrumentedStream([], gate);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ControlFrameCodec.WriteAsync(stream, RequestJson(), ControlTransportLimits.Default, cancellation.Token));
    }

    [Fact]
    public async Task SmallReadChunks_ReassembleThePayloadExactlyWithinTheChunkBound()
    {
        var json = RequestJson();
        var limits = ControlTransportLimits.Default with { ReadChunkBytes = 8 };
        var stream = new InstrumentedStream(Framed(json));

        var read = await ControlFrameCodec.ReadAsync(stream, limits);

        Assert.Equal(ControlFrameReadStatus.Frame, read.Status);
        Assert.Equal(json, read.Json);
        Assert.Equal(8, stream.MaxRequestedBufferBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(json), stream.PayloadBytesRead);
    }

    [Fact]
    public async Task DeclaredLengthNearUintMax_IsRejectedAsAnOverLimitFrameWithoutWrapping()
    {
        var stream = new InstrumentedStream(LengthPrefix(unchecked((int)uint.MaxValue)));

        var read = await ControlFrameCodec.ReadAsync(stream);

        Assert.Equal(ControlFrameReadStatus.Rejected, read.Status);
        Assert.Equal(ControlFrameRejection.DeclaredLengthTooLarge, read.Rejection);
        Assert.Equal(ControlErrorCodes.MalformedRequest, read.ErrorCode);
        Assert.Equal(4, stream.TotalBytesRead);
        Assert.Equal(0, stream.PayloadBytesRead);
    }

    /// <summary>
    /// A bounded in-memory stream that records the largest buffer it was ever asked to fill, so a test can
    /// prove that an oversized frame is rejected before any payload-sized allocation. An optional gate task
    /// defers delivery to simulate a stalled peer for the deadline cases.
    /// </summary>
    private sealed class InstrumentedStream : Stream
    {
        private readonly byte[] _content;
        private readonly TaskCompletionSource? _gate;
        private readonly List<byte> _written = [];
        private int _position;

        public InstrumentedStream(byte[] content, TaskCompletionSource? gate = null)
        {
            _content = content;
            _gate = gate;
        }

        public int MaxRequestedBufferBytes { get; private set; }

        public int TotalBytesRead { get; private set; }

        public int PayloadBytesRead => Math.Max(0, TotalBytesRead - PrefixBytesRead);

        public int PrefixBytesRead => Math.Min(TotalBytesRead, 4);

        public byte[] WrittenToArray() => _written.ToArray();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _content.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaxRequestedBufferBytes = Math.Max(MaxRequestedBufferBytes, buffer.Length);
            if (_gate is not null)
            {
                await _gate.Task.WaitAsync(cancellationToken);
            }

            var count = Math.Min(buffer.Length, _content.Length - _position);
            _content.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            TotalBytesRead += count;
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_gate is not null)
            {
                return WriteGatedAsync(buffer, cancellationToken);
            }

            _written.AddRange(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private async ValueTask WriteGatedAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            await _gate!.Task.WaitAsync(cancellationToken);
            _written.AddRange(buffer.ToArray());
        }
    }
}
