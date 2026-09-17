using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Extraction;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Core.Persistence;
using Rag.HistoricalLoader.Core.Sampling;
using Rag.HistoricalLoader.Engine.Control;
using Rag.HistoricalLoader.Engine.Pipeline;

namespace Rag.HistoricalLoader.IntegrationTests.Control;

/// <summary>
/// 11.prev-d RED — the supervised control host over a real file-backed SQLite database: the installation
/// lock, receipt replay after a restart, durable loaded rows preserved, and the rule that only an explicit
/// resume continues recovered work. The transport is the in-slice fake; no Windows pipe is opened.
/// </summary>
public sealed class ControlServiceRecoveryTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 14, 10, 11, 12, TimeSpan.Zero);

    [Fact]
    public async Task Restart_OverARealFileDatabase_PreservesReceiptsAndLoadedRows_AndOnlyAnExplicitResumeContinues()
    {
        var fixture = await Fixture.CreateAsync();
        var api = new BlockingApiClient();
        try
        {
            var commandId = Guid.NewGuid();
            var runId = Guid.NewGuid();

            // --- first process: start, pause at a durable boundary, then stop.
            var transport = new FakeControlTransport();
            var first = await ControlHost.OpenAsync(
                fixture.HostOptions(), transport, new FakeTextExtractor(), api, NewPipeline());
            var helloOfFirst = await SendAsync(transport, HelloJson());
            var accepted = await SendAsync(transport, StartJson(commandId, fixture.BatchId, runId));
            Assert.Equal("ok", Status(helloOfFirst));
            Assert.Equal("accepted", Status(accepted));
            Assert.Equal(runId.ToString(), Payload(accepted).GetProperty("run_id").GetString());

            await api.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            var paused = await SendAsync(transport, PauseJson(runId));
            Assert.Equal("accepted", Status(paused));

            api.Release();
            var beforeStop = await WaitForStateAsync(transport, runId, "paused", TimeSpan.FromSeconds(20));
            Assert.Equal(1, Count(beforeStop, "loaded"));
            Assert.Equal(1, Count(beforeStop, "pending"));
            var engineInstanceOfFirst = Payload(helloOfFirst).GetProperty("engine_instance_id").GetString();
            await first.DisposeAsync();

            // --- second process: new engine instance over the same database.
            var secondTransport = new FakeControlTransport();
            await using var second = await ControlHost.OpenAsync(
                fixture.HostOptions(), secondTransport, new FakeTextExtractor(), api, NewPipeline());
            var helloOfSecond = await SendAsync(secondTransport, HelloJson());
            Assert.NotEqual(engineInstanceOfFirst, Payload(helloOfSecond).GetProperty("engine_instance_id").GetString());

            var dispatchedBeforeReplay = api.OperationCalls;
            var replayed = await SendAsync(secondTransport, StartJson(commandId, fixture.BatchId, runId));
            Assert.Equal("accepted", Status(replayed));
            Assert.Equal(runId.ToString(), Payload(replayed).GetProperty("run_id").GetString());

            var afterReplay = await SendAsync(secondTransport, StateJson(runId));
            Assert.Equal(1, Count(Payload(afterReplay), "loaded"));
            Assert.Equal(1, Count(Payload(afterReplay), "pending"));

            // Replaying an accepted start returns its durable receipt and dispatches nothing: the document the
            // first process loaded is not re-ingested, and no second pipeline starts for the recovered run.
            Assert.Equal(dispatchedBeforeReplay, api.OperationCalls);

            var resumed = await SendAsync(secondTransport, ResumeJson(runId));
            Assert.Equal("accepted", Status(resumed));
            var completed = await WaitForStateAsync(secondTransport, runId, "completed", TimeSpan.FromSeconds(20));
            Assert.Equal(2, Count(completed, "loaded"));
            Assert.Equal(0, Count(completed, "pending"));
        }
        finally
        {
            api.Release();
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task ASecondHostOnTheSameInstallation_FailsOnTheLockWithoutOpeningItsStore()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            await using var first = await ControlHost.OpenAsync(
                fixture.HostOptions(), new FakeControlTransport(), new FakeTextExtractor(), new FakeHistoricalApiClient(), NewPipeline());

            Assert.True(first.IsListening);

            // The lock is OS-backed and exclusive, so a second host cannot take the installation over.
            Assert.Throws<IOException>(() => new FileStream(fixture.LockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

            var competingDatabase = Path.Combine(fixture.Directory, "competing.sqlite");
            var competing = await Assert.ThrowsAsync<ControlInstallationLockHeldException>(() => ControlHost.OpenAsync(
                new ControlHostOptions
                {
                    DatabasePath = competingDatabase,
                    LockFilePath = fixture.LockFilePath,
                    Service = ServiceOptions(),
                },
                new FakeControlTransport(),
                new FakeTextExtractor(),
                new FakeHistoricalApiClient(),
                NewPipeline()));

            Assert.Equal(fixture.LockFilePath, competing.LockFilePath);
            Assert.False(File.Exists(competingDatabase));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // --- wire helpers: the integration surface speaks the version-1 protocol, not engine types -------

    private static string HelloJson() => RequestJson("hello");

    private static string StartJson(Guid commandId, Guid batchId, Guid runId) => RequestJson("start", new Dictionary<string, object?>
    {
        ["command_id"] = commandId.ToString(),
        ["batch_id"] = batchId.ToString(),
        ["run_id"] = runId.ToString(),
    });

    private static string PauseJson(Guid runId) => RequestJson("pause", CommandPayload(runId));

    private static string ResumeJson(Guid runId) => RequestJson("resume", CommandPayload(runId));

    private static string StateJson(Guid runId) => RequestJson("get_state", new Dictionary<string, object?>
    {
        ["run_id"] = runId.ToString(),
    });

    private static Dictionary<string, object?> CommandPayload(Guid runId) => new()
    {
        ["command_id"] = Guid.NewGuid().ToString(),
        ["run_id"] = runId.ToString(),
    };

    private static string RequestJson(string operation, Dictionary<string, object?>? payload = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["protocol_version"] = 1,
            ["request_id"] = Guid.NewGuid().ToString(),
            ["operation"] = operation,
        };
        if (payload is not null)
        {
            request["payload"] = payload;
        }

        return JsonSerializer.Serialize(request);
    }

    /// <summary>
    /// Sends one request over its own connection. Every connection is its own protocol session, so the
    /// handshake travels on the same stream as the request it opens, and the response of interest is the
    /// last frame of that stream. A handshake is always safe to repeat: <c>hello</c> is idempotent.
    /// </summary>
    private static async Task<string> SendAsync(FakeControlTransport transport, string requestJson)
    {
        var connection = new DuplexStream(Concat(Frame(HelloJson()), Frame(requestJson)));
        Assert.True(transport.Connect(connection));
        Assert.True(await WaitUntilAsync(
            () => ReadFrames(connection.WrittenToArray()).Count >= 2, TimeSpan.FromSeconds(15)));

        var responses = ReadFrames(connection.WrittenToArray());
        Assert.Equal(2, responses.Count);
        Assert.Equal("ok", Status(responses[0]));
        return responses[^1];
    }

    private static async Task<JsonElement> WaitForStateAsync(
        FakeControlTransport transport, Guid runId, string expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = Payload(await SendAsync(transport, StateJson(runId)));
            if (state.GetProperty("observed_state").GetString() == expected)
            {
                return state;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"The run never reported '{expected}'.");
        return default;
    }

    private static int Count(JsonElement state, string documentState) =>
        state.GetProperty("document_counts").TryGetProperty(documentState, out var count) ? count.GetInt32() : 0;

    private static string? Status(string json) => JsonDocument.Parse(json).RootElement.GetProperty("status").GetString();

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.GetProperty("payload");

    private static byte[] Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

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

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    private static ControlServiceOptions ServiceOptions() => new()
    {
        EngineInstanceId = $"engine-instance-{Guid.NewGuid():N}",
        EngineVersion = "0.1.0",
        CollectionId = "legacy",
        DrainTimeout = TimeSpan.FromSeconds(30),
    };

    private static PipelineOptions NewPipeline() => new(1, 64L * 1024 * 1024, 1_000);

    /// <summary>A real, seeded file-backed database plus the reader handles the assertions read through.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string directory, string databasePath, string lockFilePath, Guid batchId, SqliteStore store)
        {
            Directory = directory;
            DatabasePath = databasePath;
            LockFilePath = lockFilePath;
            BatchId = batchId;
            Store = store;
        }

        public string Directory { get; }

        public string DatabasePath { get; }

        public string LockFilePath { get; }

        public Guid BatchId { get; }

        public SqliteStore Store { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"rag-control-integration-{Guid.NewGuid():N}");
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
            return new Fixture(directory, databasePath, Path.Combine(directory, "installation.lock"), batchId, store);
        }

        public ControlHostOptions HostOptions() => new()
        {
            DatabasePath = DatabasePath,
            LockFilePath = LockFilePath,
            Service = ServiceOptions(),
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

    /// <summary>The fake API with a gate on the first reserve, so a pipeline can be held mid-document.</summary>
    private sealed class BlockingApiClient : IHistoricalApiClient
    {
        private readonly IHistoricalApiClient _inner = new FakeHistoricalApiClient();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;

        public Task Entered => _entered.Task;

        public int OperationCalls => ((FakeHistoricalApiClient)_inner).OperationCalls;

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

    /// <summary>Duplex byte stream: one preloaded request frame in, one appended response frame out.</summary>
    private sealed class DuplexStream : Stream
    {
        private readonly MemoryStream _input;
        private readonly MemoryStream _written = new();

        public DuplexStream(byte[] input) => _input = new MemoryStream(input);

        public bool IsPendingResponse => _written.Length == 0;

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
