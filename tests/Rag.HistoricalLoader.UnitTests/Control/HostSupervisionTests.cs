using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Rag.HistoricalLoader.Contracts;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Extraction;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Core.Persistence;
using Rag.HistoricalLoader.Core.Sampling;
using Rag.HistoricalLoader.Engine.Control;
using Rag.HistoricalLoader.Engine.Pipeline;

namespace Rag.HistoricalLoader.UnitTests.Control;

/// <summary>
/// 11.prev-d RED — host supervision: the OS-backed exclusive installation lock taken before the store or
/// the transport is opened, the accept loop over the fake transport, a client disconnect that cancels only
/// IPC work, and a shutdown that requests a safe pause and drains within the configured timeout.
/// </summary>
public sealed class HostSupervisionTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 14, 10, 11, 12, TimeSpan.Zero);

    // --- installation lock --------------------------------------------------

    [Fact]
    public async Task SecondInstance_FailsOnTheExclusiveInstallationLock_BeforeOpeningTheStoreOrTheTransport()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            using var held = ControlInstallationLock.Acquire(fixture.LockFilePath);

            // A FileShare.None holder refuses every other handle, including a permissive one: the lock is
            // OS-backed, not advisory, so a second instance cannot take over.
            Assert.Throws<IOException>(() => new FileStream(fixture.LockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            Assert.False(ControlInstallationLock.TryAcquire(fixture.LockFilePath, out _));

            var databasePath = Path.Combine(fixture.Directory, "second-instance.sqlite");
            var transport = new FakeControlTransport();
            var options = new ControlHostOptions
            {
                DatabasePath = databasePath,
                LockFilePath = fixture.LockFilePath,
                Service = ServiceOptions(),
            };

            await Assert.ThrowsAsync<ControlInstallationLockHeldException>(
                () => ControlHost.OpenAsync(options, transport, new FakeTextExtractor(), new FakeHistoricalApiClient(), NewPipeline()));

            // Failing safely means failing before the store or the transport exists at all.
            Assert.False(File.Exists(databasePath));
            Assert.False(transport.IsListening);
            Assert.Empty(Directory.GetFiles(fixture.Directory, "second-instance.sqlite*"));

            held.Dispose();

            await using var host = await ControlHost.OpenAsync(
                options, transport, new FakeTextExtractor(), new FakeHistoricalApiClient(), NewPipeline());

            Assert.True(host.IsListening);
            Assert.True(transport.IsListening);
            Assert.True(File.Exists(databasePath));
            Assert.False(ControlInstallationLock.TryAcquire(fixture.LockFilePath, out _));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // --- accept loop, hello gate, disconnect --------------------------------

    [Fact]
    public async Task Host_ServesTheContractOverTheFakeTransport_AndAClientDisconnectNeverCancelsIngestion()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var transport = new FakeControlTransport();
            await using var host = await ControlHost.OpenAsync(
                fixture.HostOptions(), transport, new FakeTextExtractor(), new FakeHistoricalApiClient(), NewPipeline());

            var runId = Guid.NewGuid();
            var connection = new DuplexStream(Concat(
                Frame(RequestJson(ControlOperations.Hello)),
                Frame(RequestJson(ControlOperations.Start, new StartRequest(Guid.NewGuid().ToString(), fixture.BatchId.ToString(), runId.ToString())))));
            Assert.True(transport.Connect(connection));

            Assert.True(await WaitUntilAsync(() => host.ActiveConnectionCount == 0, TimeSpan.FromSeconds(15)));

            var responses = ReadFrames(connection.WrittenToArray());
            Assert.Equal(2, responses.Count);
            Assert.Equal(ControlStatuses.Ok, Status(responses[0]));
            Assert.Equal(ControlStatuses.Accepted, Status(responses[1]));
            Assert.Equal(runId.ToString(), Payload(responses[1]).GetProperty("run_id").GetString());

            // The peer is gone, yet the accepted command keeps running to completion: only IPC work was cancelled.
            Assert.True(await WaitUntilAsync(
                async () => (await fixture.RunStore.GetRunDocumentsAsync(runId)).All(document => document.State == DocumentState.Loaded),
                TimeSpan.FromSeconds(20)));

            var reconnect = new DuplexStream(Concat(
                Frame(RequestJson(ControlOperations.Hello)),
                Frame(RequestJson(ControlOperations.GetState, new GetStateRequest(runId.ToString())))));
            Assert.True(transport.Connect(reconnect));
            Assert.True(await WaitUntilAsync(() => host.ActiveConnectionCount == 0, TimeSpan.FromSeconds(15)));

            var reconnected = ReadFrames(reconnect.WrittenToArray());
            Assert.Equal(2, reconnected.Count);
            Assert.Equal(ControlStatuses.Ok, Status(reconnected[0]));
            Assert.Equal(ControlStatuses.Ok, Status(reconnected[1]));
            Assert.Equal(runId.ToString(), Payload(reconnected[1]).GetProperty("run_id").GetString());

            // Without a handshake the connection is refused before any operation is dispatched.
            var outOfOrder = new DuplexStream(Frame(RequestJson(ControlOperations.GetState, new GetStateRequest(runId.ToString()))));
            Assert.True(transport.Connect(outOfOrder));
            Assert.True(await WaitUntilAsync(() => host.ActiveConnectionCount == 0, TimeSpan.FromSeconds(15)));

            var refused = Assert.Single(ReadFrames(outOfOrder.WrittenToArray()));
            Assert.Equal(ControlStatuses.Rejected, Status(refused));
            Assert.Equal(ControlErrorCodes.MalformedRequest, ErrorCode(refused));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // --- shutdown: safe pause and bounded drain -----------------------------

    [Fact]
    public async Task Shutdown_RequestsASafePause_AndDrainsWithinTheConfiguredTimeout()
    {
        var fixture = await Fixture.CreateAsync();
        var api = new BlockingApiClient();
        try
        {
            var transport = new FakeControlTransport();
            await using var host = await ControlHost.OpenAsync(
                fixture.HostOptions(), transport, new FakeTextExtractor(), api, NewPipeline());

            // The configured drain default is recorded on the production option type, not implied.
            Assert.Equal(TimeSpan.FromSeconds(30), new ControlServiceOptions
            {
                EngineInstanceId = "engine-instance-default-drain",
                EngineVersion = "0.1.0",
                CollectionId = "legacy",
            }.DrainTimeout);

            var runId = await StartAsync(host, fixture, api);

            var shutdown = host.ShutdownAsync();
            Assert.True(await WaitUntilAsync(
                async () => (await fixture.Control.GetControlSnapshotAsync(runId)).DesiredState == ControlRunStates.PauseRequested,
                TimeSpan.FromSeconds(10)));
            api.Release();

            var drained = await shutdown.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(ControlDrainStatus.Drained, drained.Status);
            Assert.Equal(RunObservedState.Paused, drained.ObservedState);
            Assert.False(host.IsListening);
            Assert.False(transport.IsListening);

            var snapshot = await fixture.Control.GetControlSnapshotAsync(runId);
            Assert.Equal(ControlRunStates.Paused, snapshot.ObservedState);
            Assert.Equal(1, snapshot.DocumentCounts.GetValueOrDefault(ControlDocumentStates.Loaded));
            Assert.Equal(1, snapshot.DocumentCounts.GetValueOrDefault(ControlDocumentStates.Pending));
        }
        finally
        {
            api.Release();
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Shutdown_WhenTheDrainDeadlineExpires_LeavesRecoveryWorkInsteadOfAFabricatedPause()
    {
        var fixture = await Fixture.CreateAsync(drainTimeout: TimeSpan.FromSeconds(1));
        var api = new BlockingApiClient();
        try
        {
            var transport = new FakeControlTransport();
            await using var host = await ControlHost.OpenAsync(
                fixture.HostOptions(), transport, new FakeTextExtractor(), api, NewPipeline());

            var runId = await StartAsync(host, fixture, api);

            var drained = await host.ShutdownAsync();

            Assert.Equal(ControlDrainStatus.TimedOut, drained.Status);
            Assert.NotEqual(RunObservedState.Paused, drained.ObservedState);
            Assert.False(host.IsListening);

            // A timed-out drain leaves recovery work behind: no fabricated pause and no fabricated loaded row.
            var snapshot = await fixture.Control.GetControlSnapshotAsync(runId);
            Assert.Equal(ControlRunStates.PauseRequested, snapshot.DesiredState);
            Assert.NotEqual(ControlRunStates.Paused, snapshot.ObservedState);
            Assert.Equal(0, snapshot.DocumentCounts.GetValueOrDefault(ControlDocumentStates.Loaded));
        }
        finally
        {
            api.Release();
            await fixture.DisposeAsync();
        }
    }

    // --- helpers ------------------------------------------------------------

    private static ControlServiceOptions ServiceOptions(TimeSpan? drainTimeout = null) => new()
    {
        EngineInstanceId = $"engine-instance-{Guid.NewGuid():N}",
        EngineVersion = "0.1.0",
        CollectionId = "legacy",
        DrainTimeout = drainTimeout ?? TimeSpan.FromSeconds(30),
    };

    private static PipelineOptions NewPipeline() => new(1, 64L * 1024 * 1024, 1_000);

    private static async Task<Guid> StartAsync(ControlHost host, Fixture fixture, BlockingApiClient api)
    {
        var runId = Guid.NewGuid();
        var outcome = await host.Service.HandleAsync(Request(
            ControlOperations.Start, new StartRequest(Guid.NewGuid().ToString(), fixture.BatchId.ToString(), runId.ToString())));
        Assert.Equal(ControlStatuses.Accepted, outcome.Status);
        await api.Entered.WaitAsync(TimeSpan.FromSeconds(15));
        return runId;
    }

    private static ControlRequest Request(string operation, object? payload = null) =>
        new(ControlProtocol.Version, Guid.NewGuid().ToString(), operation,
            payload is null ? null : JsonDocument.Parse(ControlWire.Serialize(payload)).RootElement.Clone());

    private static string RequestJson(string operation, object? payload = null) =>
        ControlWire.Serialize(new ControlRequest(
            ControlProtocol.Version, Guid.NewGuid().ToString(), operation,
            payload is null ? null : JsonDocument.Parse(ControlWire.Serialize(payload)).RootElement.Clone()));

    private static byte[] Concat(params byte[][] frames)
    {
        var buffer = new byte[frames.Sum(frame => frame.Length)];
        var offset = 0;
        foreach (var frame in frames)
        {
            frame.CopyTo(buffer, offset);
            offset += frame.Length;
        }

        return buffer;
    }

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

    private static string? ErrorCode(string json)
    {
        var root = JsonDocument.Parse(json).RootElement;
        return root.TryGetProperty("error_code", out var code) ? code.GetString() : null;
    }

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.GetProperty("payload");

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout) =>
        await WaitUntilAsync(() => Task.FromResult(condition()), timeout);

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    /// <summary>A seeded database plus the reader-side store handles the assertions read through.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            string directory, string databasePath, string lockFilePath, Guid batchId,
            SqliteStore store, SqliteRunStore runStore, SqliteControlStore control)
        {
            Directory = directory;
            DatabasePath = databasePath;
            LockFilePath = lockFilePath;
            BatchId = batchId;
            Store = store;
            RunStore = runStore;
            Control = control;
        }

        public string Directory { get; }

        public string DatabasePath { get; }

        public string LockFilePath { get; }

        public Guid BatchId { get; }

        public SqliteStore Store { get; }

        public SqliteRunStore RunStore { get; }

        public SqliteControlStore Control { get; }

        public static async Task<Fixture> CreateAsync(TimeSpan? drainTimeout = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"rag-control-host-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var databasePath = Path.Combine(directory, "store.sqlite");
            var manifestId = Guid.NewGuid();
            var rootId = Guid.NewGuid();
            var batchId = Guid.NewGuid();

            var seeder = new SqliteStore(databasePath);
            await seeder.InitializeAsync();
            await seeder.AddSourceRootAsync(new SourceRoot(rootId, "seed", "/srv/seed", SeedTime));
            await seeder.CreateManifestAsync(new Manifest(manifestId, 1, ManifestState.Complete, SeedTime, SeedTime));
            var members = new List<SampleMember>();
            for (var index = 0; index < 2; index++)
            {
                var candidateId = Guid.NewGuid();
                await seeder.AddCandidateAsync(new Candidate(
                    candidateId, manifestId, rootId, $"folder/doc-{candidateId:N}.txt", ".txt", 1024, SeedTime,
                    "eligible", MetadataFingerprint: $"fingerprint-{candidateId:N}"));
                members.Add(new SampleMember(batchId, candidateId, "stratum-a", $"fingerprint-{candidateId:N}", index));
            }

            await using (var samples = new SampleSetStore(databasePath))
            {
                await samples.InitializeAsync();
                await samples.SaveAsync(
                    new SampleSet(batchId, manifestId, "v1", "pcg64", 42, 2, 1, "confidence-rules", true, true, SeedTime, "0.98"),
                    members);
            }

            await seeder.DisposeAsync();

            var store = new SqliteStore(databasePath);
            await store.InitializeAsync();
            return new Fixture(
                directory, databasePath, Path.Combine(directory, "installation.lock"), batchId,
                store, new SqliteRunStore(store), new SqliteControlStore(store))
            {
                DrainTimeout = drainTimeout,
            };
        }

        private TimeSpan? DrainTimeout { get; init; }

        public ControlHostOptions HostOptions() => new()
        {
            DatabasePath = DatabasePath,
            LockFilePath = LockFilePath,
            Service = ServiceOptions(DrainTimeout),
        };

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Store.DisposeAsync();
            }
            finally
            {
                try
                {
                    System.IO.Directory.Delete(this.Directory, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    /// <summary>The fake API with a gate on the first reserve, so the pipeline can be held mid-document.</summary>
    private sealed class BlockingApiClient : IHistoricalApiClient
    {
        private readonly IHistoricalApiClient _inner = new FakeHistoricalApiClient();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public Task<ApiOperationResult> ReserveAsync(ApiOperation operation, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                _entered.TrySetResult();
                return AwaitAsync();

                async Task<ApiOperationResult> AwaitAsync()
                {
                    await _release.Task.WaitAsync(cancellationToken);
                    return await _inner.ReserveAsync(operation, cancellationToken);
                }
            }

            return _inner.ReserveAsync(operation, cancellationToken);
        }

        public Task<ApiOperationResult> UploadAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => _inner.UploadAsync(operation, cancellationToken);

        public Task<ApiOperationResult> CommitAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => _inner.CommitAsync(operation, cancellationToken);

        public Task<ApiOperationResult> PollAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => _inner.PollAsync(operation, cancellationToken);
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
}
