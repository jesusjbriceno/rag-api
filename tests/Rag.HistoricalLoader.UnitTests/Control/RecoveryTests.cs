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
/// 11.prev-d RED — recovery across a process restart: a new engine instance is recorded and the durable
/// cursor, counts, terminal rows and attempt ceilings survive, startup never dispatches ingestion on its
/// own, a replayed receipt never restarts a recovered run, and an unavailable store is surfaced safely.
/// </summary>
public sealed class RecoveryTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 14, 10, 11, 12, TimeSpan.Zero);

    [Fact]
    public async Task Restart_RefreshesTheEngineInstance_AndNeverDispatchesIngestionWithoutAnExplicitResume()
    {
        var workspace = await Workspace.CreateAsync();
        try
        {
            var batchId = workspace.BatchId;
            var commandId = Guid.NewGuid();
            var runId = Guid.NewGuid();

            // --- process A: one document loaded, the run parked at a durable pause boundary.
            var first = await workspace.OpenAsync();
            await first.Service.HandleAsync(StartRequest(commandId, batchId, runId));
            await first.Api.Entered.WaitAsync(TimeSpan.FromSeconds(15));
            await first.Service.HandleAsync(PauseRequest(runId));
            first.Api.Release();
            await WaitForStateAsync(first, runId, ControlRunStates.Paused, TimeSpan.FromSeconds(20));

            // A terminal outcome with an exhausted attempt counter is a durable fact, not resumable work.
            var exhausted = new RunDocument(
                Guid.NewGuid(), runId, Guid.NewGuid(), "source-exhausted", DocumentState.RetryExhaustedNetwork,
                ReserveAttempts: 3, CreatedAt: SeedTime, UpdatedAt: SeedTime);
            await first.RunStore.SaveAsync(exhausted, "retry_exhausted", "transient_failure", 3, null);

            var before = await first.Control.GetControlSnapshotAsync(runId);
            var engineInstanceOfFirst = first.Options.EngineInstanceId;
            var documentsBefore = await first.RunStore.GetRunDocumentsAsync(runId);
            await first.DisposeAsync();

            // --- process B: a new engine instance, same database.
            var second = await workspace.OpenAsync();

            Assert.NotEqual(engineInstanceOfFirst, second.Options.EngineInstanceId);

            // Startup refreshes the engine identity; it never resets counters and never starts ingestion.
            var afterRestart = await second.Control.GetControlSnapshotAsync(runId);
            Assert.Equal(second.Options.EngineInstanceId, afterRestart.EngineInstanceId);
            Assert.Equal(before.EventHighWaterMark, afterRestart.EventHighWaterMark);
            Assert.Equal(
                before.DocumentCounts.OrderBy(entry => entry.Key),
                afterRestart.DocumentCounts.OrderBy(entry => entry.Key));
            Assert.Equal(0, second.Api.OperationCalls);

            // Replaying the accepted start command returns its durable receipt and restarts nothing.
            var replay = await second.Service.HandleAsync(StartRequest(commandId, batchId, runId));

            Assert.Equal(ControlStatuses.Accepted, replay.Status);
            Assert.Equal(runId.ToString(), Assert.IsType<CommandReceipt>(replay.Payload).RunId);
            Assert.Equal(0, second.Api.OperationCalls);
            Assert.Equal(
                documentsBefore.OrderBy(document => document.Id).Select(document => (document.Id, document.State)),
                (await second.RunStore.GetRunDocumentsAsync(runId))
                    .OrderBy(document => document.Id).Select(document => (document.Id, document.State)));

            // Only an explicit resume continues the remaining work.
            var resume = await second.Service.HandleAsync(ResumeRequest(Guid.NewGuid(), runId));

            Assert.Equal(ControlStatuses.Accepted, resume.Status);

            // This process's own API gate has not been consumed yet, so the resumed network stage is released
            // here: the second process is a real process with its own client, not a replayed first one.
            second.Api.Release();

            var completed = await WaitForStateAsync(second, runId, ControlRunStates.Completed, TimeSpan.FromSeconds(20));
            Assert.Equal(2, completed.DocumentCounts.GetValueOrDefault(ControlDocumentStates.Loaded));

            var preserved = Assert.Single(
                await second.RunStore.GetRunDocumentsAsync(runId), document => document.Id == exhausted.Id);
            Assert.Equal(DocumentState.RetryExhaustedNetwork, preserved.State);
            Assert.Equal(3, preserved.ReserveAttempts);
            await second.DisposeAsync();
        }
        finally
        {
            await workspace.DisposeAsync();
        }
    }

    [Fact]
    public async Task StorageFault_YieldsAStableRejectionWithoutRawExceptionText_AndLeavesNothingBehind()
    {
        var workspace = await Workspace.CreateAsync();
        try
        {
            var instance = await workspace.OpenAsync();
            var commandId = Guid.NewGuid();
            var runId = Guid.NewGuid();

            await instance.Store.DisposeAsync();

            var dispatcher = new ControlDispatcher(instance.Service);
            var result = await dispatcher.DispatchAsync(
                Request(ControlOperations.Start, new StartRequest(commandId.ToString(), workspace.BatchId.ToString(), runId.ToString())));

            // An undurable success is never sent: the mutation is rejected with a stable code and no raw text.
            // The dispatcher delegated the command and the handler rejected it, which is what a handler-level
            // rejection means at this seam; the envelope is the rejected outcome the client receives.
            Assert.Equal(ControlDispatchStatus.Handled, result.Status);
            Assert.Equal(ControlErrorCodes.CommandConflict, result.ErrorCode);
            Assert.Equal(ControlProtocol.Version, JsonDocument.Parse(result.ResponseJson!).RootElement.GetProperty("protocol_version").GetInt32());
            Assert.Equal(ControlErrorCodes.CommandConflict, JsonDocument.Parse(result.ResponseJson!).RootElement.GetProperty("error_code").GetString());
            Assert.Equal(ControlStatuses.Rejected, JsonDocument.Parse(result.ResponseJson!).RootElement.GetProperty("status").GetString());

            var json = result.ResponseJson!;
            Assert.DoesNotContain("Exception", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Sqlite", json, StringComparison.Ordinal);
            Assert.DoesNotContain("ObjectDisposed", json, StringComparison.Ordinal);
            Assert.DoesNotContain("store.sqlite", json, StringComparison.Ordinal);
            Assert.Equal(0, instance.Api.OperationCalls);
            await instance.DisposeAsync();

            // Nothing was written: reopening the database shows no run, no documents and no receipt.
            await using var reopened = new SqliteStore(workspace.DatabasePath);
            await reopened.InitializeAsync();
            Assert.Null(await new SqliteControlStore(reopened).GetCommandReceiptAsync(commandId));
            Assert.Null(await new SqliteRunStore(reopened).GetRunAsync(runId));
            Assert.Empty(await new SqliteRunStore(reopened).GetRunDocumentsAsync(runId));
        }
        finally
        {
            await workspace.DisposeAsync();
        }
    }

    // --- helpers ------------------------------------------------------------

    private static ControlRequest Request(string operation, object? payload = null) =>
        new(ControlProtocol.Version, Guid.NewGuid().ToString(), operation,
            payload is null ? null : JsonDocument.Parse(ControlWire.Serialize(payload)).RootElement.Clone());

    private static ControlRequest StartRequest(Guid commandId, Guid batchId, Guid runId) =>
        Request(ControlOperations.Start, new StartRequest(commandId.ToString(), batchId.ToString(), runId.ToString()));

    private static ControlRequest PauseRequest(Guid runId) =>
        Request(ControlOperations.Pause, new PauseRequest(Guid.NewGuid().ToString(), runId.ToString()));

    private static ControlRequest ResumeRequest(Guid commandId, Guid runId) =>
        Request(ControlOperations.Resume, new ResumeRequest(commandId.ToString(), runId.ToString()));

    private static async Task<StateSnapshot> WaitForStateAsync(Instance instance, Guid runId, string expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        StateSnapshot? snapshot = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var outcome = await instance.Service.HandleAsync(
                Request(ControlOperations.GetState, new GetStateRequest(runId.ToString())));
            Assert.Equal(ControlStatuses.Ok, outcome.Status);
            snapshot = Assert.IsType<StateSnapshot>(outcome.Payload);
            if (snapshot.ObservedState == expected)
            {
                return snapshot;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"The run never reported '{expected}'; last durable projection was '{snapshot?.ObservedState}'.");
        return snapshot!;
    }

    /// <summary>A seeded database that can be reopened as a new engine instance (a fresh process).</summary>
    private sealed class Workspace : IAsyncDisposable
    {
        private Workspace(string directory, string databasePath, Guid batchId)
        {
            Directory = directory;
            DatabasePath = databasePath;
            BatchId = batchId;
        }

        public string Directory { get; }

        public string DatabasePath { get; }

        public Guid BatchId { get; }

        public static async Task<Workspace> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"rag-control-recovery-{Guid.NewGuid():N}");
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
            return new Workspace(directory, databasePath, batchId);
        }

        /// <summary>Opens a brand-new engine instance (new store handles, new pipeline, new instance id).</summary>
        public async Task<Instance> OpenAsync()
        {
            var store = new SqliteStore(DatabasePath);
            await store.InitializeAsync();
            var runStore = new SqliteRunStore(store);
            var control = new SqliteControlStore(store);
            var api = new BlockingApiClient();
            var options = new ControlServiceOptions
            {
                EngineInstanceId = $"engine-instance-{Guid.NewGuid():N}",
                EngineVersion = "0.1.0",
                CollectionId = "legacy",
                DrainTimeout = TimeSpan.FromSeconds(10),
                DatabasePath = DatabasePath,
            };
            var pipeline = new HistoricalPipeline(
                new FakeTextExtractor(), api, runStore, new PipelineOptions(1, 64L * 1024 * 1024, 1_000));
            var service = new ControlService(store, control, runStore, pipeline, options);
            await service.StartAsync();
            return new Instance(store, runStore, control, api, service, options);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>One engine instance: its store handles, its fake API and its control service.</summary>
    private sealed class Instance : IAsyncDisposable
    {
        public Instance(
            SqliteStore store, SqliteRunStore runStore, SqliteControlStore control,
            BlockingApiClient api, ControlService service, ControlServiceOptions options)
        {
            Store = store;
            RunStore = runStore;
            Control = control;
            Api = api;
            Service = service;
            Options = options;
        }

        public SqliteStore Store { get; }

        public SqliteRunStore RunStore { get; }

        public SqliteControlStore Control { get; }

        public BlockingApiClient Api { get; }

        public ControlService Service { get; }

        public ControlServiceOptions Options { get; }

        public async ValueTask DisposeAsync()
        {
            Api.Release();
            await Service.DisposeAsync();
            await Store.DisposeAsync();
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
}
