using Rag.HistoricalLoader.Core.Extraction;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Core.Persistence;
using Rag.HistoricalLoader.Engine.Pipeline;

namespace Rag.HistoricalLoader.Engine.Control;

/// <summary>
/// Composition facts of the supervised control host: where the loader database and the installation lock
/// live, and which engine policy the supervised service applies.
/// </summary>
public sealed record ControlHostOptions
{
    /// <summary>The loader database the host opens after it holds the installation lock.</summary>
    public required string DatabasePath { get; init; }

    /// <summary>The OS-backed exclusive installation lock path.</summary>
    public required string LockFilePath { get; init; }

    /// <summary>The engine policy the supervised service applies.</summary>
    public required ControlServiceOptions Service { get; init; }
}

/// <summary>
/// The supervised control host: it takes the OS-backed exclusive installation lock first, opens the store
/// only after the lock is held, serves the version-1 contract over the transport seam with one protocol
/// session per connection, and supervises scheduling independently of any client connection.
/// </summary>
/// <remarks>
/// A client disconnect ends only that connection's IPC work: the supervised pipeline is not bound to a
/// connection token, so an accepted run keeps going to its durable boundary. Shutdown requests a safe pause
/// and drains within the configured timeout; a timeout leaves recovery work instead of a fabricated pause.
/// </remarks>
public sealed class ControlHost : IAsyncDisposable
{
    private readonly ControlInstallationLock _lock;
    private readonly SqliteStore _store;
    private readonly IControlTransport _transport;
    private readonly ControlTransportLimits _limits;
    private int _activeConnections;
    private bool _listening;

    private ControlHost(
        ControlInstallationLock heldLock,
        SqliteStore store,
        ControlService service,
        IControlTransport transport)
    {
        _lock = heldLock;
        _store = store;
        Service = service;
        _transport = transport;
        _limits = ControlTransportLimits.Default;
    }

    /// <summary>The supervised control service behind the unchanged dispatcher seam.</summary>
    public ControlService Service { get; }

    /// <summary>Whether the host is accepting connections.</summary>
    public bool IsListening => _listening;

    /// <summary>How many connection slots are currently open.</summary>
    public int ActiveConnectionCount => Volatile.Read(ref _activeConnections);

    /// <summary>
    /// Opens the supervised host. The installation lock is taken before the store or the transport exists, so a
    /// second instance fails safely without touching either; once the lock is held, the store is opened, the
    /// engine-instance identity is recorded, and the transport starts.
    /// </summary>
    /// <exception cref="ControlInstallationLockHeldException">Another engine instance holds the lock.</exception>
    public static async Task<ControlHost> OpenAsync(
        ControlHostOptions options,
        IControlTransport transport,
        IExtractor extractor,
        IHistoricalApiClient api,
        PipelineOptions pipelineOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(pipelineOptions);

        var heldLock = ControlInstallationLock.Acquire(options.LockFilePath);
        SqliteStore? store = null;
        ControlService? service = null;
        try
        {
            store = new SqliteStore(options.DatabasePath);
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var runStore = new SqliteRunStore(store);
            var control = new SqliteControlStore(store);
            var pipeline = new HistoricalPipeline(extractor, api, runStore, pipelineOptions);
            var serviceOptions = options.Service with
            {
                DatabasePath = options.Service.DatabasePath ?? options.DatabasePath,
                ConfigurationSnapshot = options.Service.ConfigurationSnapshot ?? pipelineOptions.BuildConfigurationSnapshot(),
            };

            service = new ControlService(store, control, runStore, pipeline, serviceOptions);
            await service.StartAsync(cancellationToken).ConfigureAwait(false);

            var host = new ControlHost(heldLock, store, service, transport);
            host.Listen();
            return host;
        }
        catch
        {
            if (service is not null)
            {
                await service.DisposeAsync().ConfigureAwait(false);
            }
            else if (store is not null)
            {
                await store.DisposeAsync().ConfigureAwait(false);
            }

            heldLock.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Requests a safe pause and drains the supervised run within the configured timeout, then stops accepting
    /// connections. No new connection is served after this call returned.
    /// </summary>
    public async Task<ControlDrainResult> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        StopListening();
        return await Service.DrainAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        StopListening();
        try
        {
            await Service.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Disposal is best-effort: the durable rows already describe what was committed.
        }

        try
        {
            await _store.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort cleanup of the store handle.
        }

        _lock.Dispose();
    }

    private void Listen()
    {
        _transport.Start(OnConnectionAsync);
        _listening = _transport.IsListening;
    }

    private void StopListening()
    {
        if (!_listening)
        {
            return;
        }

        _listening = false;
        _transport.Stop();
    }

    /// <summary>
    /// Serves one accepted connection: the slot is registered synchronously, one protocol session is created
    /// for this connection, and the loop ends on a clean end of stream, a failed-closed frame, or the host's
    /// own shutdown. Nothing here reaches the supervised pipeline's cancellation.
    /// </summary>
    private async Task OnConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _activeConnections) > _limits.MaxConcurrentConnections)
        {
            // Bounded connections: a saturated host refuses the peer without dispatching anything.
            Interlocked.Decrement(ref _activeConnections);
            return;
        }

        var dispatcher = new ControlDispatcher(new ControlSession(Service), _limits);
        var connection = dispatcher.TryOpenConnection();
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            while (!lifetime.IsCancellationRequested && connection is not null)
            {
                var result = await dispatcher.ProcessFrameAsync(connection, stream, lifetime.Token).ConfigureAwait(false);
                if (result.Status == ControlDispatchStatus.EndOfStream || result.CloseConnection)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The host is shutting down or the peer went away mid-frame.
        }
        catch (IOException)
        {
            // A disconnected stream ends only this connection's IPC work.
        }
        finally
        {
            if (connection is not null)
            {
                dispatcher.CloseConnection(connection);
            }

            Interlocked.Decrement(ref _activeConnections);
        }
    }
}
