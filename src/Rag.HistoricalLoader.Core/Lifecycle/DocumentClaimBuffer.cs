using System.Threading.Channels;

namespace Rag.HistoricalLoader.Core.Lifecycle;

/// <summary>
/// Bounded in-memory buffer fed by durable SQLite claims. Writers drop (and return <c>false</c>) when the
/// buffer is full so extraction never outruns the configured capacity boundary. The channel is not a
/// source of truth; the durable claim rows remain authoritative.
/// </summary>
public sealed class DocumentClaimBuffer
{
    private readonly Channel<DocumentClaim> _channel;

    public DocumentClaimBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Claim buffer capacity must be positive.");
        }

        _channel = Channel.CreateBounded<DocumentClaim>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Offers a claim without blocking; returns <c>false</c> when the buffer is full.</summary>
    public bool TryWrite(DocumentClaim claim) => _channel.Writer.TryWrite(claim);

    /// <summary>Signals that no more claims will be written.</summary>
    public void TryComplete() => _channel.Writer.TryComplete();

    /// <summary>Drains claims until the writer is completed.</summary>
    public IAsyncEnumerable<DocumentClaim> ReadAllAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
