using Rag.HistoricalLoader.Contracts;
using Rag.HistoricalLoader.Engine.Control;

namespace Rag.HistoricalLoader.Engine;

/// <summary>
/// The production control host of the engine CLI: a current-user-restricted named pipe that opens only when it
/// is explicitly started, which only the supervised <c>serve</c> composition does.
/// </summary>
/// <remarks>
/// This type owns the boundary lifetime and nothing else — it never listens on construction, delegates every
/// start and stop to the transport it holds, and reports the stable code of a boundary it could not start
/// instead of substituting a localhost TCP, public, or anonymous endpoint:
/// <list type="bullet">
/// <item>the parameterless constructor is the production path: it opens nothing, and its <c>Start</c> asks the
/// boundary for the transport exactly once. On a platform the boundary cannot serve it leaves
/// <see cref="Endpoint"/> <see langword="null"/> and reports <c>platform_not_supported</c>;</item>
/// <item>a transport that cannot own its endpoint (a competing owner of the same kernel-object name, or an ACL
/// or identity failure) leaves the host not listening and reports <c>command_conflict</c>; the engine never
/// retries under another name;</item>
/// <item>the injectable constructor takes the transport and the endpoint, which is how the portable tests and
/// the supervised <c>serve</c> composition link the host to a boundary.</item>
/// </list>
/// The Windows ACL, remote-rejection, and peer-identity code stays internal to the engine, so this public
/// surface remains portable and the 11.prev-c <c>Engine.Control</c> guard keeps holding.
/// </remarks>
public sealed class NamedPipeHost : IControlTransport
{
    private readonly object _gate = new();
    private IControlTransport? _transport;
    private ControlPipeEndpoint? _endpoint;

    /// <summary>The production host. It opens nothing until <see cref="Start"/> is called.</summary>
    public NamedPipeHost()
    {
    }

    /// <summary>The host over a given boundary. It opens nothing until <see cref="Start"/> is called.</summary>
    public NamedPipeHost(IControlTransport transport, ControlPipeEndpoint endpoint)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    /// <summary>The endpoint this host owns, or <see langword="null"/> before it is derived.</summary>
    public ControlPipeEndpoint? Endpoint => Volatile.Read(ref _endpoint);

    /// <inheritdoc />
    public bool IsListening => Volatile.Read(ref _transport)?.IsListening == true;

    /// <summary>
    /// The stable code of a start that failed closed, or <see langword="null"/> when the last start succeeded
    /// (or none was attempted yet). It is a wire value of the control contract, never raw exception text.
    /// </summary>
    public string? StartErrorCode { get; private set; }

    /// <summary>
    /// Starts the host and registers the handler that receives every accepted duplex stream. The overload
    /// without a connection token is the host-level form; the <see cref="IControlTransport"/> form carries the
    /// boundary's own connection token and is what the supervised host uses.
    /// </summary>
    public void Start(Func<Stream, Task> onConnection)
    {
        ArgumentNullException.ThrowIfNull(onConnection);
        StartCore((stream, _) => onConnection(stream));
    }

    /// <inheritdoc />
    void IControlTransport.Start(Func<Stream, CancellationToken, Task> onConnection)
    {
        ArgumentNullException.ThrowIfNull(onConnection);
        StartCore(onConnection);
    }

    private void StartCore(Func<Stream, CancellationToken, Task> onConnection)
    {
        IControlTransport? transport;
        lock (_gate)
        {
            if (IsListening)
            {
                return;
            }

            transport = _transport;
            if (transport is null)
            {
                // The production path asks the boundary for its transport exactly once; a platform that cannot
                // be served reports the contract's stable code and no endpoint is invented.
                if (!ControlPipeTransportFactory.TryCreate(out var created, out var endpoint, out var errorCode)
                    || created is null
                    || endpoint is null)
                {
                    StartErrorCode = errorCode;
                    return;
                }

                _transport = transport = created;
                _endpoint = endpoint;
            }
        }

        transport.Start(onConnection);

        if (!transport.IsListening)
        {
            // The boundary could not own the endpoint: the host claims no listener and never retries under
            // another name.
            StartErrorCode = ControlErrorCodes.CommandConflict;
            return;
        }

        StartErrorCode = null;
    }

    /// <inheritdoc />
    public void Stop()
    {
        IControlTransport? transport;
        lock (_gate)
        {
            transport = _transport;
        }

        transport?.Stop();
    }
}
