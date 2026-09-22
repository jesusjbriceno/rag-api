using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Rag.HistoricalLoader.Contracts;
using Rag.HistoricalLoader.Core.Extraction;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Engine;
using Rag.HistoricalLoader.Engine.Control;
using Rag.HistoricalLoader.Engine.Pipeline;

namespace Rag.HistoricalLoader.UnitTests.Control;

/// <summary>
/// 11.prev-e RED — the production named-pipe boundary of the local control host.
///
/// Every case runs on Linux against the transport seam and pins a fact that must hold on the platform the
/// suite runs on:
///
/// <list type="bullet">
/// <item>the production invocation fails closed with the contract's stable <c>platform_not_supported</c> code
/// before it takes the installation lock, opens the store, reads a credential, or starts any ingestion;</item>
/// <item>the endpoint is a fixed prefix plus an opaque installation/user identity, and no caller can choose
/// the pipe name or smuggle a path into it;</item>
/// <item>the boundary declares the recorded IPC limits, refuses a saturated connection without dispatching
/// it, and a pipe-name collision fails closed instead of replacing a listening endpoint;</item>
/// <item>there is no localhost TCP, public, or anonymous fallback;</item>
/// <item><see cref="NamedPipeHost"/> never listens until it is explicitly started, so the production pipe
/// opens only under <c>serve</c>;</item>
/// <item><c>serve</c> composes the full supervised host over the pipe boundary and a second instance fails
/// closed on the installation lock.</item>
/// </list>
///
/// Linux proves the seam, the fail-closed decisions, and the early cut. It does <b>not</b> prove Windows ACL
/// enforcement, same-user success, or foreign-user/remote denial: that evidence is recorded by the operator
/// in <c>docs/historical-ingestion-rebaseline/unit-11-prev-windows-pipe-security.md</c>. The Windows ACL and
/// peer-identity code therefore stays internal to the engine, so this public surface stays portable and the
/// 11.prev-c <c>Engine.Control</c> guard keeps holding.
///
/// GREEN target — the exact public surface these cases require:
/// <list type="bullet">
/// <item><c>ControlPipeEndpoint</c>: <c>FixedPrefix</c>, <c>OpaqueIdentityLength</c>,
/// <c>MaxIdentityLength</c>, <c>Derive(installationIdentity, userIdentity)</c>, <c>OpaqueIdentity</c>,
/// <c>PipeName</c>; no public constructor and no <c>Parse</c>/<c>TryParse</c>.</item>
/// <item><c>ControlPipeTransportFactory</c>: <c>IsPlatformSupported</c>, <c>Limits</c>, and
/// <c>TryCreate(out transport, out endpoint, out errorCode)</c>.</item>
/// <item><c>Rag.HistoricalLoader.Engine.NamedPipeHost</c> (replacing the non-listening stub, implementing
/// <see cref="IControlTransport"/>): the parameterless production constructor,
/// <c>NamedPipeHost(IControlTransport, ControlPipeEndpoint)</c>, <c>Endpoint</c>, <c>IsListening</c>,
/// <c>StartErrorCode</c>, <c>Start(onConnection)</c>, and <c>Stop()</c>.</item>
/// <item><c>ControlServePlan</c>: <c>DatabasePath</c>, <c>LockFilePath</c>, <c>CollectionId</c>.</item>
/// <item><c>ControlServeCommand</c>: the existing <c>RunAsync</c>/<c>UsageExitCode</c>/
/// <c>NotSupportedExitCode</c> plus <c>ConflictExitCode</c>,
/// <c>TryPlan(args, out plan, out errorCode, out exitCode)</c>, and
/// <c>ServeAsync(plan, transport, extractor, api, pipeline, cancellationToken)</c>.</item>
/// </list>
///
/// Required behaviour of that surface:
/// <list type="bullet">
/// <item><c>NamedPipeHost.Start</c> on a platform the boundary cannot serve leaves <c>Endpoint</c> null,
/// <c>IsListening</c> false, and <c>StartErrorCode</c> = <c>platform_not_supported</c>; a pipe boundary that
/// cannot own the endpoint leaves <c>StartErrorCode</c> = <c>command_conflict</c> and is never retried under
/// another name.</item>
/// <item><c>ControlServeCommand.ServeAsync</c> composes the full supervised host (installation lock, store,
/// service, pipe), returns <c>ConflictExitCode</c> when the installation lock is held, and returns the success
/// code 0 after a cancellation-requested drain.</item>
/// </list>
/// </summary>
public sealed class NamedPipeTransportTests
{
    private const string InstallationIdentity = "1f0c9a34-6b2e-4f57-9d31-0a7c5e2b84df";
    private const string ForeignInstallationIdentity = "7b41d8e2-0c93-4a16-8f52-1d6e9c0a37b5";
    private const string UserIdentity = "S-1-5-21-4110969178-1630769618-2608164491-1001";
    private const string ForeignUserIdentity = "S-1-5-21-4110969178-1630769618-2608164491-1002";

    private static readonly string[] PathShapedFragments = ["/", "\\", ":", "..", " ", "*", "?", "|"];

    // ------------------------------------------------------------------------------------------------
    // Endpoint identity: fixed prefix + opaque installation/user identity, never a caller-selected path.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheProductionEndpoint_IsAFixedPrefixPlusAnOpaqueInstallationAndUserIdentity()
    {
        var endpoint = ControlPipeEndpoint.Derive(InstallationIdentity, UserIdentity);

        Assert.StartsWith(ControlPipeEndpoint.FixedPrefix + "-", endpoint.PipeName);
        Assert.Equal(ControlPipeEndpoint.OpaqueIdentityLength, endpoint.OpaqueIdentity.Length);
        Assert.True(
            endpoint.OpaqueIdentity.All(character => char.IsAsciiDigit(character) || char.IsAsciiLetterLower(character)),
            $"The opaque identity must be lowercase hexadecimal, but was '{endpoint.OpaqueIdentity}'.");

        // The endpoint is opaque: it never carries the installation identity, the user identity, or a
        // source path, and it is a single kernel-object name rather than a path.
        Assert.DoesNotContain(InstallationIdentity, endpoint.PipeName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UserIdentity, endpoint.PipeName, StringComparison.OrdinalIgnoreCase);
        Assert.All(
            PathShapedFragments,
            fragment => Assert.DoesNotContain(fragment, endpoint.PipeName, StringComparison.Ordinal));
    }

    [Fact]
    public void EndpointDerivation_IsDeterministic_AndBindsTheNameToTheInstallationAndTheUser()
    {
        var endpoint = ControlPipeEndpoint.Derive(InstallationIdentity, UserIdentity);

        // Deterministic: a second instance of the same installation and user cannot invent a different name,
        // so it cannot address or displace the running endpoint.
        Assert.Equal(endpoint.PipeName, ControlPipeEndpoint.Derive(InstallationIdentity, UserIdentity).PipeName);

        // Bound to the user: a foreign user's endpoint is a different kernel object, so a substituted name is
        // never the current user's endpoint.
        var foreignUser = ControlPipeEndpoint.Derive(InstallationIdentity, ForeignUserIdentity);
        Assert.NotEqual(endpoint.PipeName, foreignUser.PipeName);
        Assert.NotEqual(endpoint.OpaqueIdentity, foreignUser.OpaqueIdentity);

        // Bound to the installation: two installations on one machine never share one kernel object name.
        var foreignInstallation = ControlPipeEndpoint.Derive(ForeignInstallationIdentity, UserIdentity);
        Assert.NotEqual(endpoint.PipeName, foreignInstallation.PipeName);
        Assert.NotEqual(endpoint.OpaqueIdentity, foreignInstallation.OpaqueIdentity);
    }

    [Theory]
    [InlineData(@"\\.\pipe\rag-historical-loader-v1-0123456789abcdef0123456789abcdef")]
    [InlineData(@"C:\ProgramData\rag\installation")]
    [InlineData("../../etc/passwd")]
    [InlineData("folder/installation")]
    [InlineData(@"folder\installation")]
    [InlineData("installation:stream")]
    [InlineData("installation name")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("installation|name")]
    [InlineData("installation*name")]
    [InlineData("installation?name")]
    public void EndpointDerivation_RejectsAPathShapedOrCallerSelectedIdentity(string identity)
    {
        // Identities are opaque tokens. A caller-supplied pipe path, drive path, relative path, or filename
        // is refused on either input, so the endpoint can never be chosen from outside the engine.
        Assert.Throws<ArgumentException>(() => ControlPipeEndpoint.Derive(identity, UserIdentity));
        Assert.Throws<ArgumentException>(() => ControlPipeEndpoint.Derive(InstallationIdentity, identity));
    }

    [Fact]
    public void EndpointDerivation_RejectsMissingOversizedAndControlCharacterIdentities()
    {
        Assert.Throws<ArgumentNullException>(() => ControlPipeEndpoint.Derive(null!, UserIdentity));
        Assert.Throws<ArgumentException>(() => ControlPipeEndpoint.Derive(string.Empty, UserIdentity));
        Assert.Throws<ArgumentException>(() => ControlPipeEndpoint.Derive("   ", UserIdentity));
        Assert.Throws<ArgumentException>(
            () => ControlPipeEndpoint.Derive(new string('a', ControlPipeEndpoint.MaxIdentityLength + 1), UserIdentity));
        Assert.Throws<ArgumentException>(() => ControlPipeEndpoint.Derive("line\nbreak", UserIdentity));
        Assert.Throws<ArgumentException>(() => ControlPipeEndpoint.Derive("nul\0byte", UserIdentity));

        // The bound is a bound, not an off-by-one rejection of a legitimate opaque identity.
        var longest = ControlPipeEndpoint.Derive(new string('a', ControlPipeEndpoint.MaxIdentityLength), UserIdentity);
        Assert.Equal(ControlPipeEndpoint.OpaqueIdentityLength, longest.OpaqueIdentity.Length);
    }

    [Fact]
    public void TheEndpointSurface_HasNoCallerSelectedConstructorParserOrOverride()
    {
        var endpointType = typeof(ControlPipeEndpoint);

        // An endpoint exists only through Derive(identity, identity): there is no public constructor, no
        // Parse/TryParse, and no member that takes a caller-supplied pipe name or path.
        Assert.Empty(endpointType.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(
            endpointType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.Name is "Parse" or "TryParse" or "FromPipeName" or "FromPath");

        var parameterNames = endpointType
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.Name ?? string.Empty)
            .ToArray();

        Assert.NotEmpty(parameterNames);
        Assert.All(parameterNames, name => Assert.DoesNotContain("pipe", name, StringComparison.OrdinalIgnoreCase));
        Assert.All(parameterNames, name => Assert.DoesNotContain("path", name, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------------------------------------
    // The Linux cut: platform_not_supported before the lock, the store, every credential, and ingestion.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ProductionServe_OnThisPlatform_FailsClosedBeforeTheLockTheStoreAndAnyIngestion()
    {
        var directory = TempDirectory.Create();
        try
        {
            var databasePath = Path.Combine(directory.Path, "serve.sqlite");
            var lockFilePath = Path.Combine(directory.Path, "historical-loader.lock");
            string[] args = ["serve", "--db", databasePath, "--lock", lockFilePath];

            // The platform gate is a fact of the boundary, not of the invocation, and it never substitutes a
            // fallback transport for the pipe it cannot create.
            Assert.False(ControlPipeTransportFactory.IsPlatformSupported);
            Assert.False(ControlPipeTransportFactory.TryCreate(out var transport, out var endpoint, out var transportError));
            Assert.Null(transport);
            Assert.Null(endpoint);
            Assert.Equal(ControlErrorCodes.PlatformNotSupported, transportError);

            Assert.False(ControlServeCommand.TryPlan(args, out var plan, out var planError, out var planExitCode));
            Assert.Null(plan);
            Assert.Equal(ControlErrorCodes.PlatformNotSupported, planError);
            Assert.Equal(ControlServeCommand.NotSupportedExitCode, planExitCode);

            // The production invocation returns the stable code instead of throwing. On this platform a Windows
            // credential read raises PlatformNotSupportedException, so returning at all proves that no
            // credential was touched.
            Assert.Equal(ControlServeCommand.NotSupportedExitCode, await ControlServeCommand.RunAsync(args));

            // The guard ran before the installation lock, the store, and every ingestion: this invocation
            // created nothing at all. (The composition case below is its companion — it proves the very same
            // files DO appear once a host is really composed.)
            Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Theory]
    [InlineData("--pipe")]
    [InlineData("--pipe-name")]
    [InlineData("--pipe-path")]
    [InlineData("--endpoint")]
    public async Task ProductionServe_RejectsACallerSelectedPipeEndpointOnTheCommandLine(string argument)
    {
        var directory = TempDirectory.Create();
        try
        {
            string[] args =
            [
                "serve",
                "--db", Path.Combine(directory.Path, "serve.sqlite"),
                "--lock", Path.Combine(directory.Path, "historical-loader.lock"),
                argument, @"\\.\pipe\rag-historical-loader-evil",
            ];

            Assert.False(ControlServeCommand.TryPlan(args, out var plan, out _, out var exitCode));
            Assert.Null(plan);
            Assert.Equal(ControlServeCommand.UsageExitCode, exitCode);
            Assert.Equal(ControlServeCommand.UsageExitCode, await ControlServeCommand.RunAsync(args));

            // A refused invocation opens nothing, not even the store it was pointed at.
            Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
        }
        finally
        {
            directory.Dispose();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Limits, deadlines, and saturation at the pipe boundary.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePipeBoundary_DeclaresTheRecordedIpcLimits_AndNoFallbackSurface()
    {
        // One source of truth for the IPC bounds: the pipe must not invent a looser deadline or an unbounded
        // connection count of its own.
        Assert.Equal(ControlTransportLimits.Default, ControlPipeTransportFactory.Limits);
        Assert.Equal(ControlProtocol.Limits.MaxFrameBytes, ControlPipeTransportFactory.Limits.MaxFrameBytes);
        Assert.Equal(ControlProtocol.Limits.MaxJsonDepth, ControlPipeTransportFactory.Limits.MaxJsonDepth);
        Assert.Equal(ControlProtocol.Limits.MaxPageSize, ControlPipeTransportFactory.Limits.MaxPageSize);
        Assert.True(ControlPipeTransportFactory.Limits.ReadDeadline > TimeSpan.Zero);
        Assert.True(ControlPipeTransportFactory.Limits.WriteDeadline > TimeSpan.Zero);
        Assert.True(ControlPipeTransportFactory.Limits.MaxConcurrentConnections > 0);
        Assert.True(ControlPipeTransportFactory.Limits.MaxRequestsPerConnection > 0);

        // No fallback surface: the portable boundary exposes no TCP, no Windows pipe type, no security
        // namespace, and no public/anonymous/insecure switch a caller could reach.
        var surface = new[] { typeof(ControlPipeEndpoint), typeof(ControlPipeTransportFactory), typeof(ControlServePlan), typeof(ControlServeCommand) };
        var referenced = surface
            .SelectMany(type => type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType)))
            .Concat(surface.SelectMany(type => type.GetProperties().Select(property => property.PropertyType)))
            .Select(type => type.Namespace ?? string.Empty)
            .Distinct()
            .ToArray();

        Assert.NotEmpty(referenced);
        Assert.DoesNotContain(referenced, ns => ns.StartsWith("System.Net", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, ns => ns.StartsWith("System.IO.Pipes", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, ns => ns.StartsWith("System.Security", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, ns => ns.StartsWith("System.Runtime.InteropServices", StringComparison.Ordinal));

        var memberNames = surface
            .SelectMany(type => type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(method => method.Name)
                .Concat(type.GetProperties().Select(property => property.Name)))
            .Distinct()
            .ToArray();

        Assert.NotEmpty(memberNames);
        Assert.All(
            memberNames,
            name => Assert.All(
                new[] { "anonymous", "insecure", "unauthenticated", "tcp", "fallback" },
                fragment => Assert.DoesNotContain(fragment, name, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task SaturatedPipeBoundary_RefusesTheExtraConnectionWithoutDispatchingIt()
    {
        var directory = TempDirectory.Create();
        try
        {
            var transport = new FakeControlTransport();
            var endpoint = ControlPipeEndpoint.Derive(InstallationIdentity, UserIdentity);
            var pipe = new NamedPipeHost(transport, endpoint);

            try
            {
                await using var host = await ControlHost.OpenAsync(
                    HostOptions(directory),
                    pipe,
                    new FakeTextExtractor(),
                    new FakeHistoricalApiClient(),
                    NewPipeline());

                Assert.True(pipe.IsListening);
                Assert.True(host.IsListening);

                var limit = ControlPipeTransportFactory.Limits.MaxConcurrentConnections;
                Assert.Equal(ControlTransportLimits.Default.MaxConcurrentConnections, limit);

                // Idle peers hold their slots: the host is bounded by the recorded connection count, not by an
                // unbounded accept loop.
                var idle = new List<IdleStream>();
                for (var index = 0; index < limit; index++)
                {
                    var connection = new IdleStream();
                    Assert.True(transport.Connect(connection));
                    idle.Add(connection);
                }

                Assert.Equal(limit, host.ActiveConnectionCount);

                // The next peer is refused without a single byte going back: a saturated boundary never
                // dispatches the request it cannot supervise.
                var extra = new DuplexStream(Frame(RequestJson(ControlOperations.Hello)));
                Assert.True(transport.Connect(extra));

                Assert.Equal(limit, host.ActiveConnectionCount);
                await Task.Delay(TimeSpan.FromMilliseconds(250));
                Assert.Empty(extra.WrittenToArray());
                Assert.Equal(limit, host.ActiveConnectionCount);

                // The slots are released when the peers leave, so saturation is a bound and not a leak.
                foreach (var connection in idle)
                {
                    connection.Release();
                }

                Assert.True(
                    await WaitUntilAsync(() => host.ActiveConnectionCount == 0, TimeSpan.FromSeconds(15)),
                    "The saturated connections never released their slots.");
            }
            finally
            {
                pipe.Stop();
            }
        }
        finally
        {
            directory.Dispose();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // The pipe opens only under explicit serve, and its name collision fails closed.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheProductionNamedPipeHost_OpensNothingUntilStart_AndNeverAnAnonymousOrTcpEndpoint()
    {
        var host = new NamedPipeHost();

        // Constructing the production host opens nothing: the pipe exists only after an explicit Start, which
        // only the supervised `serve` composition performs.
        Assert.False(host.IsListening);
        Assert.Null(host.Endpoint);
        Assert.Null(host.StartErrorCode);

        host.Start(_ => Task.CompletedTask);

        // Fail closed on this platform: no listener, no endpoint, and the contract's stable code instead of a
        // localhost TCP, public, or anonymous substitute.
        Assert.False(host.IsListening);
        Assert.Null(host.Endpoint);
        Assert.Equal(ControlErrorCodes.PlatformNotSupported, host.StartErrorCode);

        host.Stop();
        Assert.False(host.IsListening);
    }

    [Fact]
    public void NamedPipeHost_DoesNotListenUntilStart_AndDelegatesItsLifetimeToThePipeBoundary()
    {
        var transport = new FakeControlTransport();
        var endpoint = ControlPipeEndpoint.Derive(InstallationIdentity, UserIdentity);
        var host = new NamedPipeHost(transport, endpoint);

        Assert.False(host.IsListening);
        Assert.False(transport.IsListening);
        Assert.Equal(endpoint, host.Endpoint);
        Assert.Null(host.StartErrorCode);

        host.Start(_ => Task.CompletedTask);

        Assert.True(host.IsListening);
        Assert.True(transport.IsListening);
        Assert.Equal(endpoint, host.Endpoint);
        Assert.Null(host.StartErrorCode);

        host.Stop();

        Assert.False(host.IsListening);
        Assert.False(transport.IsListening);
    }

    [Fact]
    public void APipeNameCollision_FailsClosedInsteadOfReplacingTheListeningEndpoint()
    {
        // A competing owner of the same kernel object name is the collision case: the boundary cannot start,
        // and the host must report it rather than claim a listener it does not own or retry with another name.
        var owned = new OwnedEndpointTransport();
        var endpoint = ControlPipeEndpoint.Derive(InstallationIdentity, UserIdentity);
        var host = new NamedPipeHost(owned, endpoint);

        host.Start(_ => Task.CompletedTask);

        Assert.Equal(1, owned.StartAttempts);
        Assert.False(host.IsListening);
        Assert.False(owned.IsListening);
        Assert.Equal(ControlErrorCodes.CommandConflict, host.StartErrorCode);
    }

    // ------------------------------------------------------------------------------------------------
    // serve composes the full host: installation lock -> store -> service -> pipe, one instance at a time.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Serve_ComposesTheFullHostOverThePipeBoundary_AndASecondInstanceFailsClosedOnTheLock()
    {
        var directory = TempDirectory.Create();
        try
        {
            var plan = new ControlServePlan
            {
                DatabasePath = Path.Combine(directory.Path, "serve.sqlite"),
                LockFilePath = Path.Combine(directory.Path, "historical-loader.lock"),
                CollectionId = "legacy",
            };

            var transport = new FakeControlTransport();
            using var shutdown = new CancellationTokenSource();
            var serving = ControlServeCommand.ServeAsync(
                plan,
                transport,
                new FakeTextExtractor(),
                new FakeHistoricalApiClient(),
                NewPipeline(),
                shutdown.Token);

            Assert.True(
                await WaitUntilAsync(() => transport.IsListening, TimeSpan.FromSeconds(20)),
                "The composed host never opened the pipe boundary.");

            // The whole composition the Windows `serve` branch runs: installation lock held, store opened,
            // supervised service started, pipe listening.
            Assert.True(File.Exists(plan.LockFilePath));
            Assert.True(File.Exists(plan.DatabasePath));
            Assert.False(ControlInstallationLock.TryAcquire(plan.LockFilePath, out _));

            // The composed host serves the version-1 handshake over the pipe.
            var connection = new DuplexStream(Frame(RequestJson(ControlOperations.Hello)));
            Assert.True(transport.Connect(connection));
            Assert.True(
                await WaitUntilAsync(() => connection.WrittenToArray().Length > 0, TimeSpan.FromSeconds(15)),
                "The composed host never answered the handshake.");

            var responses = ReadFrames(connection.WrittenToArray());
            var handshake = Assert.Single(responses);
            Assert.Equal(ControlStatuses.Ok, Status(handshake));

            // A second instance fails closed on the installation lock before its own pipe is ever opened, and
            // the running instance keeps the installation.
            var secondTransport = new FakeControlTransport();
            Assert.Equal(
                ControlServeCommand.ConflictExitCode,
                await ControlServeCommand.ServeAsync(
                    plan,
                    secondTransport,
                    new FakeTextExtractor(),
                    new FakeHistoricalApiClient(),
                    NewPipeline(),
                    CancellationToken.None));

            Assert.False(secondTransport.IsListening);
            Assert.True(transport.IsListening);

            // A shutdown request drains and ends the composition with the success exit code, and the pipe is
            // closed by the host that owns it.
            shutdown.Cancel();
            Assert.Equal(0, await serving);
            Assert.False(transport.IsListening);
        }
        finally
        {
            directory.Dispose();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // The peer check must read before it impersonates, so the byte it consumed on the way is handed to the
    // host ahead of the peer's own stream. That buffer is portable, and these cases pin the part of the
    // fix a Linux run CAN prove: the peer's bytes reach the host in order, none of them is lost or
    // duplicated, and the stream refuses to pretend it can seek. The Windows ordering itself (that
    // RunAsClient succeeds only after a read) is operator evidence in
    // docs/historical-ingestion-rebaseline/unit-11-prev-windows-pipe-security.md, never a claim of this
    // suite.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePeerPrefixStream_YieldsItsPreReadBytesBeforeTheUnderlyingStream()
    {
        var inner = new MemoryStream([0x68, 0x69]);
        await using var peer = new PeerPrefixStream(inner, [0xAB, 0xCD]);

        var read = new byte[16];
        var total = 0;
        while (total < 4)
        {
            var got = await peer.ReadAsync(read.AsMemory(total), CancellationToken.None);
            Assert.True(got > 0, "a peer with buffered and inner bytes left must not report end of stream");
            total += got;
        }

        Assert.Equal(4, total);
        Assert.Equal(new byte[] { 0xAB, 0xCD, 0x68, 0x69 }, read[..4]);
    }

    [Fact]
    public async Task ThePeerPrefixStream_ReturnsThePrefixFirst_AndNeverSkipsOrDuplicatesIt()
    {
        var inner = new MemoryStream([1, 2, 3]);
        await using var peer = new PeerPrefixStream(inner, [0xAB]);

        // A buffer far larger than the prefix must still not swallow the inner bytes into the same read:
        // the buffered peer bytes are delivered first, so a caller that inspects the first read sees the
        // byte the transport consumed, not the peer's payload.
        var first = new byte[16];
        var firstRead = await peer.ReadAsync(first.AsMemory(), CancellationToken.None);
        Assert.Equal(1, firstRead);
        Assert.Equal(0xAB, first[0]);

        // ... and the very same byte is not replayed a second time.
        var rest = new byte[16];
        var restRead = await peer.ReadAsync(rest.AsMemory(), CancellationToken.None);
        Assert.True(restRead > 0);
        Assert.NotEqual(0xAB, rest[0]);
    }

    [Fact]
    public async Task ThePeerPrefixStream_PreservesByteOrderAcrossSingleByteReads()
    {
        var inner = new MemoryStream([3, 4, 5]);
        await using var peer = new PeerPrefixStream(inner, [1, 2]);

        var seen = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var got = await peer.ReadAsync(one.AsMemory(), CancellationToken.None);
            if (got == 0)
            {
                break;
            }

            seen.Add(one[0]);
        }

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, seen);
    }

    [Fact]
    public async Task ThePeerPrefixStream_ReportsEndOfStreamOnlyAfterThePrefixAndTheInnerStreamAreExhausted()
    {
        var inner = new MemoryStream([]);
        await using var peer = new PeerPrefixStream(inner, [7]);

        var buffer = new byte[4];
        Assert.Equal(1, await peer.ReadAsync(buffer.AsMemory(), CancellationToken.None));
        Assert.Equal(0, await peer.ReadAsync(buffer.AsMemory(), CancellationToken.None));
        Assert.Equal(0, await peer.ReadAsync(buffer.AsMemory(), CancellationToken.None));

        // The synchronous path the frame codec may take reports the same thing, in the same order.
        var sync = new MemoryStream([9]);
        await using var syncPeer = new PeerPrefixStream(sync, [8]);
        var syncBuffer = new byte[4];
        Assert.Equal(1, syncPeer.Read(syncBuffer));
        Assert.Equal(8, syncBuffer[0]);
        Assert.Equal(1, syncPeer.Read(syncBuffer));
        Assert.Equal(9, syncBuffer[0]);
        Assert.Equal(0, syncPeer.Read(syncBuffer));
    }

    [Fact]
    public async Task ThePeerPrefixStream_WithNoPrefix_BehavesAsTheUnderlyingStream()
    {
        var inner = new MemoryStream([0x41, 0x42]);
        await using var peer = new PeerPrefixStream(inner, []);

        var buffer = new byte[4];
        Assert.Equal(2, await peer.ReadAsync(buffer.AsMemory(), CancellationToken.None));
        Assert.Equal(new byte[] { 0x41, 0x42 }, buffer[..2]);
        Assert.Equal(0, await peer.ReadAsync(buffer.AsMemory(), CancellationToken.None));
    }

    [Fact]
    public async Task ThePeerPrefixStream_PassesWritesThrough_AndRefusesToSeek()
    {
        var inner = new MemoryStream();
        await using var peer = new PeerPrefixStream(inner, [0xAB]);

        await peer.WriteAsync(new byte[] { 1, 2, 3 }.AsMemory(), CancellationToken.None);
        await peer.FlushAsync();
        Assert.Equal(new byte[] { 1, 2, 3 }, inner.ToArray());

        Assert.True(peer.CanRead);
        Assert.True(peer.CanWrite);
        Assert.False(peer.CanSeek);
        Assert.Throws<NotSupportedException>(() => peer.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => peer.SetLength(0));
        Assert.Throws<NotSupportedException>(() => peer.Length);
        Assert.Throws<NotSupportedException>(() => peer.Position);
    }

    // --- helpers ------------------------------------------------------------

    private static ControlHostOptions HostOptions(TempDirectory directory) =>
        new()
        {
            DatabasePath = Path.Combine(directory.Path, "pipe-host.sqlite"),
            LockFilePath = Path.Combine(directory.Path, "historical-loader.lock"),
            Service = ServiceOptions(),
        };

    private static ControlServiceOptions ServiceOptions() => new()
    {
        EngineInstanceId = $"engine-instance-{Guid.NewGuid():N}",
        EngineVersion = "0.1.0",
        CollectionId = "legacy",
        DrainTimeout = TimeSpan.FromSeconds(30),
    };

    private static PipelineOptions NewPipeline() => new(1, 64L * 1024 * 1024, 1_000);

    private static string RequestJson(string operation, object? payload = null) =>
        ControlWire.Serialize(new ControlRequest(
            ControlProtocol.Version,
            Guid.NewGuid().ToString(),
            operation,
            payload is null ? null : JsonDocument.Parse(ControlWire.Serialize(payload)).RootElement.Clone()));

    private static byte[] Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    private static IReadOnlyList<string> ReadFrames(byte[] buffer)
    {
        var frames = new List<string>();
        var offset = 0;
        while (offset + 4 <= buffer.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
            offset += 4;
            if (length < 0 || offset + length > buffer.Length)
            {
                break;
            }

            frames.Add(Encoding.UTF8.GetString(buffer, offset, length));
            offset += length;
        }

        return frames;
    }

    private static string? Status(string json) => JsonDocument.Parse(json).RootElement.GetProperty("status").GetString();

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    /// <summary>A throwaway directory for one case: the only place a case is allowed to write.</summary>
    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rag-named-pipe-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup: a store handle may still be releasing.
            }
        }
    }

    /// <summary>Duplex byte stream: preloaded request frames in, appended response frames out.</summary>
    private sealed class DuplexStream : Stream
    {
        private readonly MemoryStream _input;
        private readonly MemoryStream _written = new();

        public DuplexStream(byte[] input) => _input = new MemoryStream(input);

        public byte[] WrittenToArray() => _written.ToArray();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _input.Length;

        public override long Position
        {
            get => _input.Position;
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _written.WriteAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>An idle peer: its reads block until the case releases them, so it holds a connection slot.</summary>
    private sealed class IdleStream : Stream
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly MemoryStream _written = new();

        public void Release() => _released.TrySetResult();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _released.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _written.WriteAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A boundary whose endpoint is already owned: it starts nothing and reports no listener.</summary>
    private sealed class OwnedEndpointTransport : IControlTransport
    {
        public bool IsListening => false;

        public int StartAttempts { get; private set; }

        public void Start(Func<Stream, CancellationToken, Task> onConnection) => StartAttempts++;

        public void Stop()
        {
        }
    }
}
