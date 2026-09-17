namespace Rag.HistoricalLoader.Engine.Control;

/// <summary>
/// The byte-stream transport seam of the supervised control host. A transport owns only one fact — whether it
/// is listening — and hands every accepted duplex stream to the host's connection handler. It carries no
/// ingestion policy, no store handle, and no credential: the concrete Windows named-pipe transport is a later
/// slice, and this slice composes the host against the in-memory fake below.
/// </summary>
public interface IControlTransport
{
    /// <summary>Whether the transport currently accepts connections.</summary>
    bool IsListening { get; }

    /// <summary>
    /// Starts listening and registers the handler that receives every accepted duplex stream. The handler is
    /// invoked synchronously when a connection is accepted, so a caller that has handed over a stream can
    /// observe the connection as active before <see cref="FakeControlTransport.Connect"/> returns.
    /// </summary>
    void Start(Func<Stream, CancellationToken, Task> onConnection);

    /// <summary>Stops listening. In-flight streams are ended by the host's own lifetime token.</summary>
    void Stop();
}

/// <summary>
/// The in-slice transport: an in-memory stand-in that is handed duplex streams directly by a test (or by an
/// in-process host smoke run). It opens no pipe, no socket, and no Windows handle, so the service, host, and
/// supervision behaviour of this slice are exercised on Linux. The production transport is a later slice.
/// </summary>
public sealed class FakeControlTransport : IControlTransport
{
    private readonly object _gate = new();
    private Func<Stream, CancellationToken, Task>? _onConnection;
    private CancellationTokenSource? _connections;

    /// <inheritdoc />
    public bool IsListening { get; private set; }

    /// <inheritdoc />
    public void Start(Func<Stream, CancellationToken, Task> onConnection)
    {
        ArgumentNullException.ThrowIfNull(onConnection);
        lock (_gate)
        {
            _connections?.Cancel();
            _connections = new CancellationTokenSource();
            _onConnection = onConnection;
            IsListening = true;
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        CancellationTokenSource? connections;
        lock (_gate)
        {
            IsListening = false;
            _onConnection = null;
            connections = _connections;
            _connections = null;
        }

        connections?.Cancel();
        connections?.Dispose();
    }

    /// <summary>
    /// Hands one duplex stream to the host. Returns <see langword="false"/> when the transport is not
    /// listening, so a caller can never invent a connection that no host supervises.
    /// </summary>
    public bool Connect(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Func<Stream, CancellationToken, Task> handler;
        CancellationToken token;
        lock (_gate)
        {
            if (!IsListening || _onConnection is not { } registered || _connections is null)
            {
                return false;
            }

            handler = registered;
            token = _connections.Token;
        }

        // Synchronous hand-off: the host registers the connection slot before this call returns, so the
        // connection is observable (and drainable) immediately — never a background race.
        _ = handler(stream, token);
        return true;
    }
}
