using Microsoft.Data.Sqlite;
using Rag.HistoricalLoader.Core.Extraction;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Core.Persistence;
using Rag.HistoricalLoader.Engine.Pipeline;

namespace Rag.HistoricalLoader.IntegrationTests.Sqlite;

public sealed class LifecyclePersistenceTests
{
    // --- Durability configuration ------------------------------------------

    [Fact]
    public async Task Initialize_SetsWALMode_AndPreservesRunAcrossReopen()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            var runId = Guid.NewGuid();

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var config = await store.GetDurabilityConfigurationAsync();
                Assert.Equal("wal", config.JournalMode);

                var runStore = new SqliteRunStore(store);
                await runStore.CreateRunAsync(NewRun(runId));
                await runStore.AddRunDocumentAsync(NewDocument(runId, DocumentState.Pending));
            }

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);

                var run = await runStore.GetRunAsync(runId);
                Assert.NotNull(run);
                Assert.Equal("legacy", run.CollectionId);

                var documents = await runStore.GetRunDocumentsAsync(runId);
                var document = Assert.Single(documents);
                Assert.Equal(DocumentState.Pending, document.State);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Initialize_MigratesSchemaV4WithBackupBeforeMigration()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            SeedVersionTwoDatabase(path);

            await using var store = new SqliteStore(path);
            await store.InitializeAsync();

            Assert.Equal(4, await store.GetSchemaVersionAsync());
            Assert.True(File.Exists($"{path}.pre-migration-v2.bak"));

            var runStore = new SqliteRunStore(store);
            await runStore.CreateRunAsync(NewRun());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // --- Transaction / audit atomicity -------------------------------------

    [Fact]
    public async Task Transition_PersistsStateAndAuditInOneTransaction()
    {
        var directory = NewDirectory();
        try
        {
            await using var store = new SqliteStore(DatabasePath(directory));
            await store.InitializeAsync();
            var runStore = new SqliteRunStore(store);

            var run = NewRun();
            await runStore.CreateRunAsync(run);

            var document = NewDocument(run.Id, DocumentState.Pending);
            await runStore.AddRunDocumentAsync(document);

            await runStore.SaveAsync(
                document with { State = DocumentState.Staged, NormalizedTextHash = "sha256", UpdatedAt = DateTimeOffset.UtcNow },
                action: "staged",
                outcomeCode: null,
                attemptNumber: 0,
                measurements: null);

            var persisted = await runStore.GetRunDocumentAsync(document.Id);
            Assert.NotNull(persisted);
            Assert.Equal(DocumentState.Staged, persisted.State);
            Assert.Equal("sha256", persisted.NormalizedTextHash);

            var audit = await runStore.GetAuditEventsAsync(run.Id);
            var staged = Assert.Single(audit, e => e.Action == "staged");
            Assert.Equal("pending->staged", staged.StateTransition);
            Assert.Equal(document.Id, staged.CandidateId);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // --- Single-writer behavior --------------------------------------------

    [Fact]
    public async Task ConcurrentTransitions_AreSerializedThroughSingleWriter()
    {
        var directory = NewDirectory();
        try
        {
            await using var store = new SqliteStore(DatabasePath(directory));
            await store.InitializeAsync();
            var runStore = new SqliteRunStore(store);

            var run = NewRun();
            await runStore.CreateRunAsync(run);

            var documents = Enumerable.Range(0, 40)
                .Select(_ => NewDocument(run.Id, DocumentState.Pending))
                .ToArray();

            var writes = documents.Select(d => runStore.AddRunDocumentAsync(d)).ToArray();
            await Task.WhenAll(writes);

            var transitions = documents.Select(d =>
                runStore.SaveAsync(
                    d with { State = DocumentState.Snapshotting, UpdatedAt = DateTimeOffset.UtcNow },
                    action: "advance",
                    outcomeCode: null,
                    attemptNumber: 0,
                    measurements: null))
                .ToArray();

            await Task.WhenAll(transitions);

            var counts = await runStore.CountByStateAsync(run.Id);
            Assert.Equal(40, counts.GetValueOrDefault(DocumentState.Snapshotting));
            Assert.Equal(0, counts.GetValueOrDefault(DocumentState.Pending));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // --- Restart reconciliation / corrupt staging ---------------------------

    [Fact]
    public async Task Reconcile_RecoversFromEveryDurableBoundary_WithoutResettingTerminalStates()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            var runId = Guid.NewGuid();
            var documentIds = new Dictionary<DocumentState, Guid>();

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);

                await runStore.CreateRunAsync(NewRun(runId));

                foreach (var state in new[]
                {
                    DocumentState.Pending,
                    DocumentState.Snapshotting,
                    DocumentState.Staged,
                    DocumentState.Uploading,
                    DocumentState.RemotePending,
                    DocumentState.Loaded,
                    DocumentState.RetryWait,
                    DocumentState.SkippedDocumentError,
                    DocumentState.RetryExhaustedNetwork,
                })
                {
                    var document = NewDocument(runId, state);
                    if (state == DocumentState.Staged)
                    {
                        document = document with { NormalizedTextHash = "sha256" };
                    }

                    await runStore.AddRunDocumentAsync(document);
                    documentIds[state] = document.Id;
                }
            }

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);
                var engine = new DocumentLifecycleEngine(new FakeTextExtractor(), new FakeHistoricalApiClient(), runStore);

                var reconciled = await engine.ReconcileAsync(runId, CancellationToken.None);

                // Transient local/remote-in-flight work returns to pending.
                Assert.Contains(reconciled, d => d.Id == documentIds[DocumentState.Snapshotting] && d.State == DocumentState.Pending);
                Assert.Contains(reconciled, d => d.Id == documentIds[DocumentState.Uploading] && d.State == DocumentState.Pending);
                Assert.Contains(reconciled, d => d.Id == documentIds[DocumentState.RetryWait] && d.State == DocumentState.Pending);
                Assert.Equal(3, reconciled.Count);

                // Durable/terminal states are preserved.
                Assert.Equal(DocumentState.Pending, (await runStore.GetRunDocumentAsync(documentIds[DocumentState.Pending]))!.State);
                Assert.Equal(DocumentState.Staged, (await runStore.GetRunDocumentAsync(documentIds[DocumentState.Staged]))!.State);
                Assert.Equal(DocumentState.RemotePending, (await runStore.GetRunDocumentAsync(documentIds[DocumentState.RemotePending]))!.State);
                Assert.Equal(DocumentState.Loaded, (await runStore.GetRunDocumentAsync(documentIds[DocumentState.Loaded]))!.State);
                Assert.Equal(DocumentState.SkippedDocumentError, (await runStore.GetRunDocumentAsync(documentIds[DocumentState.SkippedDocumentError]))!.State);
                Assert.Equal(DocumentState.RetryExhaustedNetwork, (await runStore.GetRunDocumentAsync(documentIds[DocumentState.RetryExhaustedNetwork]))!.State);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Reconcile_DowngradesCorruptStagingReferenceToPending()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            var runId = Guid.NewGuid();
            var corruptStagedId = Guid.NewGuid();

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);

                await runStore.CreateRunAsync(NewRun(runId));

                // A staged document with no durable hash reference is corrupt.
                await runStore.AddRunDocumentAsync(new RunDocument(
                    corruptStagedId,
                    runId,
                    Guid.NewGuid(),
                    "source-corrupt",
                    DocumentState.Staged,
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: DateTimeOffset.UtcNow));
            }

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);
                var engine = new DocumentLifecycleEngine(new FakeTextExtractor(), new FakeHistoricalApiClient(), runStore);

                var reconciled = await engine.ReconcileAsync(runId, CancellationToken.None);

                var recovered = Assert.Single(reconciled);
                Assert.Equal(corruptStagedId, recovered.Id);
                Assert.Equal(DocumentState.Pending, recovered.State);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // --- Pause drain --------------------------------------------------------

    [Fact]
    public async Task Pause_CommitsPauseRequested_StopsClaiming_AndDrainsToPaused()
    {
        var directory = NewDirectory();
        try
        {
            await using var store = new SqliteStore(DatabasePath(directory));
            await store.InitializeAsync();
            var runStore = new SqliteRunStore(store);
            var engine = new DocumentLifecycleEngine(new FakeTextExtractor(), new FakeHistoricalApiClient(), runStore);

            var run = NewRun();
            await runStore.CreateRunAsync(run);

            var pending = NewDocument(run.Id, DocumentState.Pending);
            await runStore.AddRunDocumentAsync(pending);

            var inFlight = NewDocument(run.Id, DocumentState.Snapshotting);
            await runStore.AddRunDocumentAsync(inFlight);

            // Before pause, claiming works.
            var claim = await engine.TryClaimNextAsync(run.Id, CancellationToken.None);
            Assert.NotNull(claim);
            Assert.Equal(pending.Id, claim.RunDocumentId);

            // Pause commits pause_requested and stops claiming new documents.
            var paused = await engine.PauseAsync(run.Id, CancellationToken.None);
            Assert.Equal(RunDesiredState.PauseRequested, paused.DesiredState);
            Assert.Equal(RunObservedState.Pausing, paused.ObservedState);

            Assert.Null(await engine.TryClaimNextAsync(run.Id, CancellationToken.None));

            // The in-flight snapshotting document still lacks a durable receipt.
            Assert.False(await engine.CanReportPausedAsync(run.Id, CancellationToken.None));

            // Active work reaches a durable safe boundary (staged).
            await runStore.SaveAsync(
                inFlight with { State = DocumentState.Staged, NormalizedTextHash = "sha256", UpdatedAt = DateTimeOffset.UtcNow },
                action: "staged",
                outcomeCode: null,
                attemptNumber: 0,
                measurements: null);

            Assert.True(await engine.CanReportPausedAsync(run.Id, CancellationToken.None));

            var completed = await engine.CompletePauseAsync(run.Id, CancellationToken.None);
            Assert.Equal(RunObservedState.Paused, completed.ObservedState);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // --- Bounded claim buffer ----------------------------------------------

    [Fact]
    public async Task ClaimBuffer_IsBounded_AndYieldsWrittenClaims()
    {
        var buffer = new DocumentClaimBuffer(capacity: 2);

        var first = new DocumentClaim(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "source-1");
        var second = new DocumentClaim(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "source-2");
        var third = new DocumentClaim(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "source-3");

        Assert.True(buffer.TryWrite(first));
        Assert.True(buffer.TryWrite(second));
        Assert.False(buffer.TryWrite(third));
        buffer.TryComplete();

        var received = new List<DocumentClaim>();
        await foreach (var claim in buffer.ReadAllAsync(CancellationToken.None))
        {
            received.Add(claim);
        }

        Assert.Equal(2, received.Count);
        Assert.Contains(received, c => c.SourceDocumentKey == "source-1");
        Assert.Contains(received, c => c.SourceDocumentKey == "source-2");
    }

    // --- Durable staged/committed watermark accounting ----------------------

    [Fact]
    public async Task WatermarkAccounting_IsDurableAcrossReopen()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            var runId = Guid.NewGuid();
            var firstSourceKey = string.Empty;
            var secondSourceKey = string.Empty;

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);
                await runStore.CreateRunAsync(NewRun(runId));

                // Two staged documents; the first later commits.
                var first = NewDocument(runId, DocumentState.Pending);
                firstSourceKey = first.SourceDocumentKey;
                await runStore.AddRunDocumentAsync(first);
                await runStore.SaveStagedAsync(first, "sha256-a", 100);

                var second = NewDocument(runId, DocumentState.Pending);
                secondSourceKey = second.SourceDocumentKey;
                await runStore.AddRunDocumentAsync(second);
                await runStore.SaveStagedAsync(second, "sha256-b", 50);
                await runStore.SaveLoadedAsync(second, "op-1");
            }

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);

                var rows = await runStore.GetWatermarkRowsAsync(runId);

                Assert.Equal(2, rows.Count);
                Assert.Contains(rows, r => r.SourceDocumentKey == firstSourceKey && !r.Committed && r.StagedBytes == 100);
                Assert.Contains(rows, r => r.SourceDocumentKey == secondSourceKey && r.Committed && r.StagedBytes == 50);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // --- Durable watermark rehydration across reopen -------------------------

    [Fact]
    public async Task WatermarkRehydration_RestoresDurableStagedAndCommittedWatermarks_AfterReopen()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            var runId = Guid.NewGuid();
            long committedBytes = 0;
            long stagedBytes = 0;

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);
                await runStore.CreateRunAsync(NewRun(runId));

                // One staged document that never commits and one that reaches Loaded.
                var staged = NewDocument(runId, DocumentState.Pending);
                await runStore.AddRunDocumentAsync(staged);
                await runStore.SaveStagedAsync(staged, "sha256-staged", stagedBytes = 128);

                var loaded = NewDocument(runId, DocumentState.Pending);
                await runStore.AddRunDocumentAsync(loaded);
                await runStore.SaveStagedAsync(loaded, "sha256-loaded", committedBytes = 64);
                await runStore.SaveLoadedAsync(loaded, "op-1");
            }

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);

                // A restart builds a fresh scheduler and rehydrates it from the durable rows only.
                var scheduler = new WatermarkScheduler(stagedByteWatermark: 100_000, stagedCountWatermark: 100);
                Assert.Equal(0, scheduler.CommittedCount);
                Assert.Equal(0, scheduler.StagedCount);

                scheduler.Rehydrate(await runStore.GetWatermarkRowsAsync(runId));

                Assert.Equal(stagedBytes, scheduler.StagedBytes);
                Assert.Equal(1, scheduler.StagedCount);
                Assert.Equal(committedBytes, scheduler.CommittedBytes);
                Assert.Equal(1, scheduler.CommittedCount);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task WatermarkRehydration_RestoresCapacity_ForRowsThatNeverRecordedAMeasurement()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            var runId = Guid.NewGuid();
            var stagedSourceKey = string.Empty;
            var loadedSourceKey = string.Empty;

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);
                await runStore.CreateRunAsync(NewRun(runId));

                // Durable rows whose byte measurement never arrived: the state and the normalized hash are
                // durable, the staged byte accounting is missing (the crash window this work unit closes).
                var staged = NewDocument(runId, DocumentState.Staged) with { NormalizedTextHash = "sha256-unmeasured-staged" };
                stagedSourceKey = staged.SourceDocumentKey;
                await runStore.AddRunDocumentAsync(staged);

                var loaded = NewDocument(runId, DocumentState.Loaded) with { NormalizedTextHash = "sha256-unmeasured-loaded" };
                loadedSourceKey = loaded.SourceDocumentKey;
                await runStore.AddRunDocumentAsync(loaded);
            }

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);

                var rows = await runStore.GetWatermarkRowsAsync(runId);

                // A durable staged row is never dropped from accounting just because its measurement is
                // missing: the staged slot stays occupied and the required bytes are not invented.
                Assert.Equal(2, rows.Count);
                Assert.Contains(rows, r => r.SourceDocumentKey == stagedSourceKey && !r.Committed && r.StagedBytes == 0);
                Assert.Contains(rows, r => r.SourceDocumentKey == loadedSourceKey && r.Committed && r.StagedBytes == 0);

                var scheduler = new WatermarkScheduler(stagedByteWatermark: 100_000, stagedCountWatermark: 100);
                scheduler.Rehydrate(rows);

                Assert.Equal(1, scheduler.StagedCount);
                Assert.Equal(0, scheduler.StagedBytes);
                Assert.Equal(1, scheduler.CommittedCount);
                Assert.Equal(0, scheduler.CommittedBytes);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task RecordStagedBytes_IsDurable_AndDoesNotChangeDocumentState()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            var runId = Guid.NewGuid();
            var sourceKey = string.Empty;
            var documentId = Guid.NewGuid();

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);
                await runStore.CreateRunAsync(NewRun(runId));

                var document = NewDocument(runId, DocumentState.RemotePending, documentId);
                sourceKey = document.SourceDocumentKey;
                await runStore.AddRunDocumentAsync(document);

                // The pipeline records staged bytes without regressing the document's durable state.
                Assert.True(await runStore.RecordStagedBytesAsync(runId, sourceKey, 256));
                Assert.False(await runStore.RecordStagedBytesAsync(runId, "source-unknown", 256));
            }

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);

                var document = await runStore.GetRunDocumentAsync(documentId);
                Assert.Equal(DocumentState.RemotePending, document!.State);

                var rows = await runStore.GetWatermarkRowsAsync(runId);
                var row = Assert.Single(rows);
                Assert.Equal(sourceKey, row.SourceDocumentKey);
                Assert.False(row.Committed);
                Assert.Equal(256, row.StagedBytes);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // --- Helpers ------------------------------------------------------------

    private static Run NewRun(Guid? id = null)
        => new(
            id ?? Guid.NewGuid(),
            SampleId: Guid.NewGuid(),
            CollectionId: "legacy",
            DesiredState: RunDesiredState.Running,
            ObservedState: RunObservedState.Running,
            EngineVersion: "0.1.0",
            ConfigurationSnapshot: "{}",
            StartedAt: DateTimeOffset.UtcNow);

    private static RunDocument NewDocument(Guid runId, DocumentState state, Guid? id = null)
        => new(
            id ?? Guid.NewGuid(),
            runId,
            CandidateId: Guid.NewGuid(),
            SourceDocumentKey: $"source-{Guid.NewGuid():N}",
            State: state,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private static void SeedVersionTwoDatabase(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE loader_installation (installation_id TEXT PRIMARY KEY, schema_version INTEGER NOT NULL, created_at INTEGER NOT NULL);";
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 2;";
            command.ExecuteNonQuery();
        }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rag-lifecycle-int-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string DatabasePath(string directory) => Path.Combine(directory, "store.sqlite");

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; the OS temp directory will reclaim leftovers.
        }
    }
}
