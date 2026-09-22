using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Rag.HistoricalLoader.Contracts;

namespace Rag.HistoricalLoader.Engine.Control;

/// <summary>
/// The Windows named-pipe boundary of the local control host. Everything the portable control surface must not
/// carry lives here and stays <c>internal</c>: Windows pipe creation, the owner-restricted ACL, the explicit
/// remote-client rejection, and the peer-identity verification. The portable surface therefore keeps the
/// 11.prev-c property (no pipe, security, or interop type is reachable from the public contract), and a
/// non-Windows platform can never reach the Windows path at all.
/// </summary>
/// <remarks>
/// Fail-closed rules implemented here, in order:
/// <list type="number">
/// <item>the platform gate refuses every non-Windows platform before any Windows API exists;</item>
/// <item>the current user's SID and a machine installation identity are read before the endpoint is derived,
/// so the name cannot be chosen outside the engine; a failure to read either one serves nothing;</item>
/// <item>the first pipe instance is created with <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c>, so a competing owner of
/// the same kernel-object name fails the creation instead of being replaced, and no second name is ever
/// attempted;</item>
/// <item>the pipe is created with <c>PIPE_REJECT_REMOTE_CLIENTS</c> and an owner-restricted, protected DACL
/// that grants the current user's SID and nothing else;</item>
/// <item>every accepted peer's token SID is compared to this process's user SID before a single byte is
/// parsed or dispatched; an unverifiable peer is denied. Windows only permits the impersonation that reads
/// that token after the peer has written, so exactly one byte is read first and replayed to the host
/// unparsed.</item>
/// </list>
/// The ACL, the remote rejection, and the peer identity are enforced by the operating system, not by this
/// engine's convention, and none of them is provable on Linux: the operator record in
/// <c>docs/historical-ingestion-rebaseline/unit-11-prev-windows-pipe-security.md</c> owns that evidence.
/// </remarks>
internal static class WindowsNamedPipeBoundary
{
    /// <summary>
    /// Whether this boundary can serve. Windows only, and only a Windows whose kernel supports the explicit
    /// remote-client rejection flag (Windows 8 / Server 2012 and later). The guard annotation makes the
    /// platform check machine-verified at every Windows call site below instead of leaving it a comment.
    /// </summary>
    [SupportedOSPlatformGuard("windows")]
    internal static bool IsSupported => OperatingSystem.IsWindows() && OperatingSystem.IsWindowsVersionAtLeast(6, 2);

    /// <summary>
    /// Creates the production transport and its endpoint, or reports the stable platform code. The endpoint is
    /// derived from the machine installation identity and the current user's SID; neither input is a path, a
    /// credential, or a caller-supplied value.
    /// </summary>
    internal static bool TryCreate(
        out IControlTransport? transport,
        out ControlPipeEndpoint? endpoint,
        out string errorCode)
    {
        transport = null;
        endpoint = null;
        errorCode = ControlErrorCodes.PlatformNotSupported;

        if (!IsSupported)
        {
            return false;
        }

        try
        {
            var userSid = WindowsPipeIdentity.CurrentUserSid();
            var installationIdentity = WindowsPipeIdentity.MachineInstallationIdentity();
            endpoint = ControlPipeEndpoint.Derive(installationIdentity, userSid);
            transport = new WindowsNamedPipeTransport(endpoint, ControlTransportLimits.Default, userSid);
            errorCode = string.Empty;
            return true;
        }
        catch (Exception)
        {
            // A boundary whose user or installation identity cannot be read cannot name an endpoint it can
            // vouch for, and it must never substitute a guessed name: it serves nothing.
            transport = null;
            endpoint = null;
            errorCode = ControlErrorCodes.PlatformNotSupported;
            return false;
        }
    }
}

/// <summary>
/// The two trusted identities behind the endpoint name: the current process token's user SID, and the machine
/// installation identity. Both are read locally and neither is a secret: the SID is the same identity the ACL
/// and the peer check use, and the machine installation GUID is a machine-wide, non-secret installation
/// identifier (never a credential, a store handle, or an installation lock).
/// </summary>
internal static class WindowsPipeIdentity
{
    private const string MachineInstallationKeyPath = @"SOFTWARE\Microsoft\Cryptography";
    private const string MachineInstallationValueName = "MachineGuid";

    /// <summary>The current process token's user SID, as an opaque token.</summary>
    [SupportedOSPlatform("windows")]
    internal static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException("The current process token carries no user identity.");
        return user.Value;
    }

    /// <summary>
    /// The machine installation identity: the machine's installation GUID. It identifies the installation
    /// without opening the loader database (the platform gate runs before the installation lock and the store)
    /// and without reading any credential.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static string MachineInstallationIdentity()
    {
        using var key = Registry.LocalMachine.OpenSubKey(MachineInstallationKeyPath, writable: false)
            ?? throw new InvalidOperationException("The machine installation identity is unavailable.");
        var value = key.GetValue(MachineInstallationValueName) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("The machine installation identity is unavailable.");
        }

        return value;
    }
}

/// <summary>
/// The owner-restricted pipe security descriptor. The DACL is protected (no inherited or implicit access) and
/// grants full control to exactly one identity: the current user. SYSTEM and the built-in Administrators group
/// are deliberately absent — administrators retain the ability to take ownership, which is an operating-system
/// property this engine cannot remove, and that residual is recorded in the operator evidence file instead of
/// being hidden behind a broader DACL.
/// </summary>
internal static class WindowsPipeSecurity
{
    [SupportedOSPlatform("windows")]
    internal static PipeSecurity CurrentUserOnly(string userSid)
    {
        var sid = new SecurityIdentifier(userSid);
        var security = new PipeSecurity();
        security.SetOwner(sid);
        security.SetGroup(sid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}

/// <summary>
/// The narrow Win32 surface the boundary needs: create one named-pipe instance with an explicit security
/// descriptor and an explicit pipe mode. It is used instead of the managed convenience overloads because the
/// managed ones do not let this engine set the remote-client rejection flag, and because a competing owner of
/// the same name must be reported as a collision rather than silently retried.
/// </summary>
internal static class WindowsNamedPipeInterop
{
    private const uint AccessDuplex = 0x00000003;
    private const uint FlagOverlapped = 0x40000000;
    private const uint FlagFirstPipeInstance = 0x00080000;

    /// <summary>Byte type, byte read mode, and blocking wait are all zero-valued mode bits.</summary>
    private const uint PipeModeByteAndWait = 0x00000000;

    /// <summary>Explicitly refuses a remote client; the boundary never honours a network peer.</summary>
    private const uint RejectRemoteClients = 0x00000008;

    private const int ErrorAccessDenied = 5;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorPipeBusy = 231;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;

        public IntPtr Descriptor;

        public int InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outBufferSize,
        uint inBufferSize,
        uint defaultTimeout,
        SecurityAttributes securityAttributes);

    /// <summary>
    /// Creates one asynchronous byte-mode duplex instance carrying the given security descriptor. A competing
    /// owner of the same name — or any other creation failure — reports <see langword="false"/>; the boundary
    /// never retries under another name.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static bool TryCreateInstance(
        string fullPipeName,
        bool firstInstance,
        int maxInstances,
        int bufferBytes,
        PipeSecurity security,
        out NamedPipeServerStream? instance,
        out bool nameCollision)
    {
        instance = null;
        nameCollision = false;

        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var descriptorPointer = Marshal.AllocHGlobal(descriptor.Length);
        var handle = IntPtr.Zero;
        try
        {
            Marshal.Copy(descriptor, 0, descriptorPointer, descriptor.Length);
            var attributes = new SecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = descriptorPointer,
                InheritHandle = 0,
            };

            var openMode = AccessDuplex
                | FlagOverlapped
                | (firstInstance ? FlagFirstPipeInstance : 0u);

            handle = CreateNamedPipe(
                fullPipeName,
                openMode,
                PipeModeByteAndWait | RejectRemoteClients,
                (uint)maxInstances,
                (uint)bufferBytes,
                (uint)bufferBytes,
                defaultTimeout: 0u,
                attributes);

            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                var error = Marshal.GetLastWin32Error();
                nameCollision = error is ErrorAccessDenied or ErrorAlreadyExists or ErrorPipeBusy;
                handle = IntPtr.Zero;
                return false;
            }

            var owned = new SafePipeHandle(handle, ownsHandle: true);
            try
            {
                // Overlapped handle ⇒ the managed wrapper must be created as an asynchronous stream.
                instance = new NamedPipeServerStream(
                    PipeDirection.InOut,
                    isAsync: true,
                    isConnected: false,
                    owned);
                handle = IntPtr.Zero;
                return true;
            }
            catch (Exception)
            {
                owned.Dispose();
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptorPointer);
        }
    }
}

/// <summary>
/// The Windows production transport of the control boundary. It owns only the endpoint and the pipe instances;
/// every security decision (ACL, remote rejection, peer identity) is enforced before the accepted stream
/// reaches the supervised host, and the host keeps owning the protocol session, the connection bound, and the
/// refusal of a saturated peer.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsNamedPipeTransport : IControlTransport
{
    private readonly ControlPipeEndpoint _endpoint;
    private readonly ControlTransportLimits _limits;
    private readonly string _userSid;
    private readonly PipeSecurity _security;
    private readonly object _gate = new();
    private CancellationTokenSource _lifetime = new();
    private Func<Stream, CancellationToken, Task>? _onConnection;
    private NamedPipeServerStream? _pending;
    private Task? _acceptLoop;

    internal WindowsNamedPipeTransport(
        ControlPipeEndpoint endpoint,
        ControlTransportLimits limits,
        string userSid)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _userSid = userSid ?? throw new ArgumentNullException(nameof(userSid));
        _security = WindowsPipeSecurity.CurrentUserOnly(userSid);
    }

    /// <inheritdoc />
    public bool IsListening { get; private set; }

    /// <inheritdoc />
    public void Start(Func<Stream, CancellationToken, Task> onConnection)
    {
        ArgumentNullException.ThrowIfNull(onConnection);

        lock (_gate)
        {
            if (IsListening)
            {
                return;
            }

            // A restart after Stop gets a fresh lifetime; otherwise the previous cancellation would leave the
            // flag set on a boundary that can no longer accept anything.
            if (_lifetime.IsCancellationRequested)
            {
                _lifetime = new CancellationTokenSource();
            }

            _onConnection = onConnection;

            // The very first instance carries FILE_FLAG_FIRST_PIPE_INSTANCE: if another owner already holds
            // this kernel-object name, creation fails and this call leaves the boundary not listening.
            if (!TryCreateInstanceLocked(firstInstance: true))
            {
                _onConnection = null;
                return;
            }

            IsListening = true;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_lifetime.Token), CancellationToken.None);
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        NamedPipeServerStream? pending;
        lock (_gate)
        {
            IsListening = false;
            _onConnection = null;
            pending = _pending;
            _pending = null;
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Stopping twice is a no-op.
        }

        // Disposing the waiting instance breaks a pending WaitForConnectionAsync so the accept loop ends now
        // instead of waiting for a peer that may never arrive.
        DisposeInstance(pending);
    }
    /// <summary>
    /// Accepts one peer at a time, verifies it, and hands the accepted stream to the host. A cycle that cannot
    /// create its next instance stops listening: a boundary that cannot own the endpoint must never claim it.
    /// </summary>
    private async Task AcceptLoopAsync(CancellationToken lifetime)
    {
        while (!lifetime.IsCancellationRequested)
        {
            NamedPipeServerStream current;
            Func<Stream, CancellationToken, Task> handler;
            lock (_gate)
            {
                if (!IsListening || _pending is null || _onConnection is null)
                {
                    return;
                }

                current = _pending;
                handler = _onConnection;
            }


            try
            {
                await current.WaitForConnectionAsync(lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                DisposeInstance(current);
                return;
            }
            catch (ObjectDisposedException)
            {
                // Stop disposed the waiting instance.
                return;
            }
            catch (IOException)
            {
                // The instance can no longer accept: fail closed instead of creating a replacement name.
                DisposeInstance(current);
                StopListening();
                return;
            }

            lock (_gate)
            {
                if (ReferenceEquals(_pending, current))
                {
                    _pending = null;
                }
            }

            // A fresh instance is created before the peer is verified, so the next peer always has a
            // listener and a peer that connects without ever sending anything cannot hold the accept loop. A
            // failure here stops accepting rather than weakening the endpoint.
            var continuesAccepting = PrepareNextInstance();

            // The peer check needs a byte. On a byte-mode pipe the operating system refuses to impersonate a
            // client until data has been read from that pipe, so the boundary reads exactly one byte and hands
            // it back to the host, unparsed, through PeerPrefixStream. A peer that sends nothing inside the
            // read deadline, or whose token SID is not this process's, is denied before any byte is parsed or
            // dispatched and before the host registers a connection slot.
            var prefix = await TryReadPeerPrefixAsync(current, lifetime).ConfigureAwait(false);
            if (prefix is null || !TryVerifyPeer(current))
            {
                DisposeInstance(current);
                if (!continuesAccepting)
                {
                    return;
                }

                continue;
            }

            _ = DispatchAsync(current, new PeerPrefixStream(current, prefix), handler, lifetime);
            if (!continuesAccepting)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Reads the one byte the peer check must consume before the operating system will let this boundary
    /// impersonate the client, or <see langword="null"/> when the peer sent nothing inside the read deadline.
    /// The byte is returned to the host afterwards, never parsed here.
    /// </summary>
    private async Task<byte[]?> TryReadPeerPrefixAsync(NamedPipeServerStream instance, CancellationToken lifetime)
    {
        var prefix = new byte[1];

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        deadline.CancelAfter(_limits.ReadDeadline);
        try
        {
            var read = await instance.ReadAsync(prefix.AsMemory(), deadline.Token).ConfigureAwait(false);
            return read == prefix.Length ? prefix : null;
        }
        catch (Exception)
        {
            // A peer that left, stalled past the deadline, or faulted the read is denied: this boundary never
            // verifies a peer it could not hear from.
            return null;
        }
    }

    /// <summary>
    /// Serves one verified connection and then ends it. The host owns every connection fault; a peer failure
    /// must never end the accept loop.
    /// </summary>
    private async Task DispatchAsync(
        NamedPipeServerStream instance,
        Stream peer,
        Func<Stream, CancellationToken, Task> handler,
        CancellationToken lifetime)
    {
        try
        {
            await handler(peer, lifetime).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The host already maps a protocol or transport fault to a stable outcome; this loop only owns
            // the lifetime of the instance it handed over.
        }
        finally
        {
            try
            {
                if (instance.IsConnected)
                {
                    instance.Disconnect();
                }
            }
            catch (Exception)
            {
                // Disconnecting a peer that already left is a no-op.
            }

            DisposeInstance(instance);
        }
    }

    /// <summary>
    /// Verifies the connected peer: the client is impersonated and its token's user SID must be this process's
    /// user SID. A peer the engine cannot impersonate or read is denied.
    /// </summary>
    /// <remarks>
    /// Impersonation is only possible once the peer has written to the pipe — the operating system refuses it
    /// on a connection with no data — so the caller reads one byte first and replays it through
    /// <see cref="PeerPrefixStream"/>. The window that existed before is gone: a refused connection carries no
    /// frame because no byte is parsed until this check has passed.
    /// </remarks>
    private bool TryVerifyPeer(NamedPipeServerStream instance)
    {
        try
        {
            var sameUser = false;
            instance.RunAsClient(() =>
            {
                using var client = WindowsIdentity.GetCurrent();
                sameUser = string.Equals(client.User?.Value, _userSid, StringComparison.Ordinal);
            });
            return sameUser;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Stops listening without disposing an already dispatched peer.</summary>
    private void StopListening()
    {
        NamedPipeServerStream? pending;
        lock (_gate)
        {
            IsListening = false;
            _onConnection = null;
            pending = _pending;
            _pending = null;
        }

        DisposeInstance(pending);
    }

    /// <summary>Creates the next instance for the next peer, or stops listening when it cannot be owned.</summary>
    private bool PrepareNextInstance()
    {
        lock (_gate)
        {
            if (!IsListening)
            {
                return false;
            }

            if (!TryCreateInstanceLocked(firstInstance: false))
            {
                IsListening = false;
                _onConnection = null;
                return false;
            }

            return true;
        }
    }

    private bool TryCreateInstanceLocked(bool firstInstance)
    {
        if (_pending is not null)
        {
            return true;
        }

        var created = WindowsNamedPipeInterop.TryCreateInstance(
            FullPipeName(),
            firstInstance,
            _limits.MaxConcurrentConnections + 1,
            _limits.ReadChunkBytes,
            _security,
            out var instance,
            out _);

        _pending = instance;
        return created && instance is not null;
    }

    /// <summary>The Windows kernel-object namespace plus the derived endpoint name.</summary>
    private string FullPipeName() => string.Concat(@"\\.\pipe\", _endpoint.PipeName);

    private static void DisposeInstance(NamedPipeServerStream? instance)
    {
        if (instance is null)
        {
            return;
        }

        try
        {
            instance.Dispose();
        }
        catch (Exception)
        {
            // Best-effort release of one instance handle.
        }
    }
}
