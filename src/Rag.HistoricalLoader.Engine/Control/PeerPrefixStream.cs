namespace Rag.HistoricalLoader.Engine.Control;

/// <summary>
/// A duplex stream that yields bytes the transport already read from a peer before it yields the peer's own
/// stream: in order, once each, and with nothing invented.
/// </summary>
/// <remarks>
/// This type exists for exactly one Windows fact. On a byte-mode named pipe the operating system refuses to
/// impersonate a client until data has been read from that pipe, so <c>RunAsClient</c> — which is how the
/// boundary compares the peer's token user SID with its own — cannot succeed on a freshly accepted
/// connection. The transport therefore reads one byte to become able to verify the peer, and it must not
/// discard what it read: a version-1 frame is a four-byte length prefix followed by a JSON payload with no
/// resynchronisation point, so losing a byte would corrupt every frame on that connection.
///
/// The order the boundary guarantees is the security-relevant one and it is unchanged by this buffer: no byte
/// is <i>parsed</i> and no request is <i>dispatched</i> for a peer whose SID has not been checked. The byte the
/// check consumed is held here and replayed to the host only after that check passed.
///
/// Two deliberate limits: the stream refuses seeking instead of pretending to support it, and it does not own
/// the inner stream, because the transport owns every accepted instance's lifetime and disposing it twice from
/// two places would close a handle the accept loop still has to dispose exactly once.
/// </remarks>
public sealed class PeerPrefixStream : Stream
{
    private const string NoLength =
        "A peer stream is a live connection, so it has no length and no position.";

    private readonly Stream _inner;
    private readonly byte[] _prefix;
    private int _delivered;

    /// <summary>
    /// Wraps <paramref name="inner"/> so that <paramref name="prefix"/> is delivered first. The prefix is
    /// copied, so a caller cannot mutate the bytes after handing them over.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.</exception>
    public PeerPrefixStream(Stream inner, ReadOnlySpan<byte> prefix)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _prefix = prefix.ToArray();
    }

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException(NoLength);

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException(NoLength);
        set => throw new NotSupportedException(NoLength);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
        => Read(new Span<byte>(buffer, offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        // A read that has buffered peer bytes returns only those bytes: delivering the peer's own stream in the
        // same call would hide the boundary between them from a caller that inspects what it just received.
        if (TryReadPrefix(buffer, out var delivered))
        {
            return delivered;
        }

        return _inner.Read(buffer);
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (TryReadPrefix(buffer.Span, out var delivered))
        {
            return ValueTask.FromResult(delivered);
        }

        return _inner.ReadAsync(buffer, cancellationToken);
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
        => Write(new ReadOnlySpan<byte>(buffer, offset, count));

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(NoLength);

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException(NoLength);

    /// <summary>
    /// Releases this wrapper only. The inner stream stays open because the transport owns the accepted
    /// instance's lifetime and disposes it once on its own path.
    /// </summary>
    protected override void Dispose(bool disposing) => base.Dispose(disposing);

    /// <summary>
    /// Copies out as much of the not-yet-delivered prefix as the caller's buffer can hold, or reports that the
    /// prefix is exhausted and the inner stream should answer instead.
    /// </summary>
    private bool TryReadPrefix(Span<byte> buffer, out int delivered)
    {
        delivered = 0;
        if (buffer.IsEmpty || _delivered >= _prefix.Length)
        {
            return false;
        }

        var available = Math.Min(buffer.Length, _prefix.Length - _delivered);
        _prefix.AsSpan(_delivered, available).CopyTo(buffer);
        _delivered += available;
        delivered = available;
        return true;
    }
}
