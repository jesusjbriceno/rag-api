using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rag.HistoricalLoader.Core.Benchmark;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Core.Persistence;
using Rag.HistoricalLoader.Core.Sampling;

namespace Rag.HistoricalLoader.UnitTests.Lifecycle;

/// <summary>
/// 11.prev-b1a — the additive local-only schema v4 migration (backup before migration), the backward
/// compatibility of the pre-existing v3 surface, and the durable engine-instance record.
/// </summary>
/// <remarks>
/// The seeded database is built from a frozen copy of the v1–v3 DDL held by this test file, so the
/// additive-only proof never depends on the migration code under test. The sampling and benchmark tables
/// are created by their own stores (as they are in production). Only the v4 surface
/// (<c>loader_command</c>, <c>loader_engine_instance</c>) is expected to appear, and every pre-existing
/// object and row must stay byte-identical.
/// </remarks>
public sealed class ControlStoreTests
{
    private const string FrozenSchemaV3 = """
        CREATE TABLE loader_installation (installation_id TEXT PRIMARY KEY, schema_version INTEGER NOT NULL, created_at INTEGER NOT NULL);
        CREATE TABLE source_root (root_id TEXT PRIMARY KEY, label TEXT NOT NULL, canonical_path TEXT NOT NULL, created_at INTEGER NOT NULL, reparse_traversal_disabled INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE manifest (manifest_id TEXT PRIMARY KEY, scan_version INTEGER NOT NULL, state INTEGER NOT NULL, start_time INTEGER NOT NULL, end_time INTEGER);
        CREATE TABLE candidate (candidate_id TEXT PRIMARY KEY, manifest_id TEXT NOT NULL REFERENCES manifest(manifest_id), root_id TEXT NOT NULL REFERENCES source_root(root_id), relative_path TEXT NOT NULL, extension TEXT NOT NULL, byte_size INTEGER NOT NULL, last_write_time INTEGER NOT NULL, eligibility_code TEXT NOT NULL, discovery_error_code TEXT, metadata_fingerprint TEXT, UNIQUE (manifest_id, root_id, relative_path));
        CREATE TABLE audit_event (event_id INTEGER PRIMARY KEY AUTOINCREMENT, utc_timestamp INTEGER NOT NULL, run_id TEXT, candidate_id TEXT, action TEXT NOT NULL, state_transition TEXT, outcome_code TEXT, attempt_number INTEGER NOT NULL DEFAULT 0, measurements TEXT);
        CREATE TABLE enumeration_error (error_id INTEGER PRIMARY KEY AUTOINCREMENT, manifest_id TEXT NOT NULL REFERENCES manifest(manifest_id), root_id TEXT NOT NULL REFERENCES source_root(root_id), relative_path TEXT NOT NULL, error_code TEXT NOT NULL, utc_timestamp INTEGER NOT NULL);
        CREATE INDEX idx_enumeration_error_manifest ON enumeration_error(manifest_id);
        CREATE TABLE run (run_id TEXT PRIMARY KEY, sample_id TEXT, collection_id TEXT NOT NULL, desired_state INTEGER NOT NULL, observed_state INTEGER NOT NULL, engine_version TEXT NOT NULL, configuration_snapshot TEXT NOT NULL, started_at INTEGER NOT NULL, ended_at INTEGER, checkpoint_at INTEGER);
        CREATE TABLE run_document (run_document_id TEXT PRIMARY KEY, run_id TEXT NOT NULL REFERENCES run(run_id), candidate_id TEXT NOT NULL, source_document_key TEXT NOT NULL, state INTEGER NOT NULL, reserve_attempts INTEGER NOT NULL DEFAULT 0, upload_attempts INTEGER NOT NULL DEFAULT 0, commit_attempts INTEGER NOT NULL DEFAULT 0, poll_attempts INTEGER NOT NULL DEFAULT 0, extraction_hash TEXT, normalized_text_hash TEXT, remote_upload_id TEXT, remote_document_id TEXT, remote_version_id TEXT, remote_operation_id TEXT, terminal_classification TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, UNIQUE (run_id, candidate_id));
        CREATE INDEX idx_run_document_run_state ON run_document(run_id, state);
        """;

    private static readonly string[] FrozenTables =
    [
        "loader_installation",
        "source_root",
        "manifest",
        "candidate",
        "audit_event",
        "enumeration_error",
        "run",
        "run_document",
        "sample_set",
        "sample_set_member",
        "benchmark_observation",
        "benchmark_report",
    ];

    private static readonly string[] V4Tables = ["loader_command", "loader_engine_instance"];

    private static readonly Guid SeedInstallationId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid SeedRootId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid SeedManifestId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid SeedCandidateId = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid SeedRunId = Guid.Parse("55555555-5555-4555-8555-555555555555");
    private static readonly Guid SeedDocumentId = Guid.Parse("66666666-6666-4666-8666-666666666666");
    private static readonly Guid SeedSampleSetId = Guid.Parse("77777777-7777-4777-8777-777777777777");
    private static readonly Guid SeedObservationId = Guid.Parse("88888888-8888-4888-8888-888888888888");
    private static readonly Guid NewerRunId = Guid.Parse("55555555-5555-4555-8555-555555555591");
    private static readonly Guid OtherManifestId = Guid.Parse("33333333-3333-4333-8333-333333333332");
    private static readonly Guid GreaterManifestId = Guid.Parse("33333333-3333-4333-8333-333333333334");

    // --- Migration + backup evidence ----------------------------------------

    [Fact]
    public async Task Initialize_MigratesSeededV3ToV4_WithBackupTakenBeforeMigration()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            SeedFrozenV3Database(path);
            await SeedSampleAndBenchmarkTablesAsync(path);
            var before = ReadSchemaObjects(path);

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                Assert.Equal(4, await store.GetSchemaVersionAsync());
                Assert.True((await store.GetDurabilityConfigurationAsync()).ForeignKeysEnabled);
            }

            var backupPath = $"{path}.pre-migration-v3.bak";
            Assert.True(File.Exists(backupPath));

            // The backup holds the pre-migration state, so it was taken before the v4 DDL ran.
            Assert.Equal(3, ReadUserVersion(backupPath));

            Assert.All(V4Tables, table => Assert.True(TableExists(path, table), $"{table} was not created"));
            Assert.Equal(0L, CountRows(path, "loader_command"));
            Assert.Equal(0L, CountRows(path, "loader_engine_instance"));

            // Additive-only: nothing pre-existing disappeared or changed, and exactly the v4 tables appeared.
            var after = ReadSchemaObjects(path);
            Assert.NotEmpty(before);
            Assert.Empty(before.Keys.Except(after.Keys));
            Assert.Equal(V4Tables, after.Keys.Except(before.Keys).OrderBy(name => name, StringComparer.Ordinal).ToArray());
            foreach (var (name, ddl) in before)
            {
                Assert.Equal(ddl, after[name]);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // --- Backward compatibility ---------------------------------------------

    [Fact]
    public async Task Initialize_PreservesEveryPreExistingRowByteIdentical_AndKeepsV3ReadSurfacesWorking()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            SeedFrozenV3Database(path);
            await SeedSampleAndBenchmarkTablesAsync(path);
            var before = FrozenTables.ToDictionary(table => table, table => ReadTableDump(path, table));

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);

                Assert.Equal(SeedInstallationId, await store.GetInstallationIdAsync());
                Assert.Equal(ManifestState.Complete, await store.GetManifestStateAsync(SeedManifestId));
                Assert.Equal(1, await store.CountCandidatesAsync(SeedManifestId));

                var error = Assert.Single(await store.GetEnumerationErrorsAsync(SeedManifestId));
                Assert.Equal("access_denied", error.ErrorCode);
                Assert.Equal("locked/b.txt", error.RelativePath);

                var run = await runStore.GetRunAsync(SeedRunId);
                Assert.NotNull(run);
                Assert.Equal("seed-collection", run.CollectionId);
                Assert.NotNull(run.CheckpointAt);

                var document = Assert.Single(await runStore.GetRunDocumentsAsync(SeedRunId));
                Assert.Equal(DocumentState.Staged, document.State);
                Assert.Equal(2, document.ReserveAttempts);
                Assert.Equal(1, document.UploadAttempts);
                Assert.Equal("sha256-seed", document.NormalizedTextHash);

                var audit = Assert.Single(await runStore.GetAuditEventsAsync(SeedRunId));
                Assert.Equal("staged", audit.Action);
                Assert.Equal("pending->staged", audit.StateTransition);
                Assert.Equal("128", audit.Measurements);
            }

            // The sampling and benchmark stores keep reading their own pre-existing rows unchanged.
            await using (var sampleStore = new SampleSetStore(path))
            {
                await sampleStore.InitializeAsync();
                var (sampleSet, members) = await sampleStore.LoadAsync(SeedSampleSetId);
                Assert.Equal(SeedManifestId, sampleSet.ManifestId);
                Assert.Equal(42, sampleSet.Seed);
                Assert.Equal("95/10", sampleSet.ConfidenceCoverageRules);
                Assert.Single(members);
                Assert.Equal("txt|small", members[0].StratumKey);
            }

            await using (var benchmarkStore = new BenchmarkObservationStore(path))
            {
                await benchmarkStore.InitializeAsync();
                Assert.Equal(1, await benchmarkStore.CountObservationsAsync(SeedSampleSetId));
            }

            var after = FrozenTables.ToDictionary(table => table, table => ReadTableDump(path, table));
            Assert.Contains("sha256-seed", before["run_document"], StringComparison.Ordinal);
            Assert.Contains("staged", before["audit_event"], StringComparison.Ordinal);
            Assert.Contains("95/10", before["sample_set"], StringComparison.Ordinal);
            Assert.Contains("txt|small", before["sample_set_member"], StringComparison.Ordinal);
            Assert.Contains("txt|small", before["benchmark_observation"], StringComparison.Ordinal);
            Assert.All(FrozenTables, table => Assert.Equal(before[table], after[table]));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Initialize_SecondRunOnMigratedDatabase_IsNoOpWithoutFurtherBackup()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            string[] v4Ddl;

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);
                await runStore.CreateRunAsync(NewRun());
                v4Ddl = ReadV4Ddl(path);
            }

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                Assert.Equal(4, await store.GetSchemaVersionAsync());
                Assert.Equal(1L, CountRows(path, "run"));
            }

            Assert.Empty(Directory.GetFiles(directory, "*.bak"));
            Assert.Equal(v4Ddl, ReadV4Ddl(path));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Initialize_WhenBackupStepFails_FailsClosedAtV3WithoutV4Tables()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            SeedFrozenV3Database(path);
            var rowsBefore = ReadTableDump(path, "run_document");

            // The backup destination cannot be opened as a database, so the backup step itself fails.
            var backupPath = $"{path}.pre-migration-v3.bak";
            Directory.CreateDirectory(backupPath);

            var store = new SqliteStore(path);
            await Assert.ThrowsAsync<SqliteException>(() => store.InitializeAsync());
            await store.DisposeAsync();

            Assert.True(Directory.Exists(backupPath));
            Assert.Equal(3, ReadUserVersion(path));
            Assert.All(V4Tables, table => Assert.False(TableExists(path, table), $"{table} survived a failed migration"));
            Assert.Equal(rowsBefore, ReadTableDump(path, "run_document"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // --- Engine instance ----------------------------------------------------

    [Fact]
    public async Task RecordEngineInstanceAsync_RecordsAndRefreshesIdentity_WithoutResettingCounters()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            await using var store = new SqliteStore(path);
            await store.InitializeAsync();
            var runStore = new SqliteRunStore(store);
            var controlStore = new SqliteControlStore(store);

            await controlStore.RecordEngineInstanceAsync("engine-i1");
            var recorded = await controlStore.GetEngineInstanceAsync();
            Assert.NotNull(recorded);
            Assert.Equal("engine-i1", recorded.EngineInstanceId);

            var (runId, documentId) = await SeedDurableWorkAsync(runStore);
            var documentsBefore = await runStore.GetRunDocumentsAsync(runId);
            var auditBefore = await runStore.GetAuditEventsAsync(runId);
            Assert.Single(documentsBefore);
            Assert.Single(auditBefore);

            await controlStore.RecordEngineInstanceAsync("engine-i2");

            var refreshed = await controlStore.GetEngineInstanceAsync();
            Assert.NotNull(refreshed);
            Assert.Equal("engine-i2", refreshed.EngineInstanceId);

            // A refresh replaces the identity row; it never appends history and never resets durable work.
            Assert.Equal(1L, CountRows(path, "loader_engine_instance"));

            var documentsAfter = await runStore.GetRunDocumentsAsync(runId);
            var auditAfter = await runStore.GetAuditEventsAsync(runId);
            var document = Assert.Single(documentsAfter);
            Assert.Equal(documentId, document.Id);
            Assert.Equal(DocumentState.Staged, document.State);
            Assert.Equal(2, document.ReserveAttempts);
            Assert.Equal(1, document.UploadAttempts);
            Assert.Equal(documentsBefore.Count, documentsAfter.Count);
            Assert.Equal(auditBefore.Count, auditAfter.Count);
            Assert.Equal("staged", Assert.Single(auditAfter).Action);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task RecordEngineInstanceAsync_AfterReopen_RefreshesIdentityAndKeepsDurableCounters()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            Guid runId;

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);
                var controlStore = new SqliteControlStore(store);

                (runId, _) = await SeedDurableWorkAsync(runStore);
                await controlStore.RecordEngineInstanceAsync("engine-i1");
            }

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);
                var controlStore = new SqliteControlStore(store);

                var persisted = await controlStore.GetEngineInstanceAsync();
                Assert.NotNull(persisted);
                Assert.Equal("engine-i1", persisted.EngineInstanceId);

                await controlStore.RecordEngineInstanceAsync("engine-i2");

                var refreshed = await controlStore.GetEngineInstanceAsync();
                Assert.NotNull(refreshed);
                Assert.Equal("engine-i2", refreshed.EngineInstanceId);

                var document = Assert.Single(await runStore.GetRunDocumentsAsync(runId));
                Assert.Equal(DocumentState.Staged, document.State);
                Assert.Equal(2, document.ReserveAttempts);
                Assert.Equal(1, document.UploadAttempts);
                Assert.Single(await runStore.GetAuditEventsAsync(runId));
                Assert.Equal(1L, CountRows(path, "loader_engine_instance"));
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task RecordEngineInstanceAsync_OnMigratedV3Database_RecordsAfterMigrationAndRejectsBlankIds()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            SeedFrozenV3Database(path);
            var rowsBefore = ReadTableDump(path, "run_document");

            await using var store = new SqliteStore(path);
            await store.InitializeAsync();
            var controlStore = new SqliteControlStore(store);

            // A freshly migrated database has no recorded engine instance yet.
            Assert.Null(await controlStore.GetEngineInstanceAsync());

            await controlStore.RecordEngineInstanceAsync("engine-migrated");
            var recorded = await controlStore.GetEngineInstanceAsync();
            Assert.NotNull(recorded);
            Assert.Equal("engine-migrated", recorded.EngineInstanceId);

            // A blank identity is rejected before the write, so the recorded identity is unchanged.
            await Assert.ThrowsAsync<ArgumentException>(() => controlStore.RecordEngineInstanceAsync("   "));
            Assert.Equal("engine-migrated", (await controlStore.GetEngineInstanceAsync())!.EngineInstanceId);

            Assert.Equal(0L, CountRows(path, "loader_command"));
            Assert.Equal(rowsBefore, ReadTableDump(path, "run_document"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // --- Command receipts: atomicity, replay, conflict, rejection (11.prev-b1b) ---

    [Fact]
    public async Task CommitStartAsync_CommitsRunDocumentsOperatorAuditAndReceiptInOneTransaction()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            await using var store = new SqliteStore(path);
            await store.InitializeAsync();
            var runStore = new SqliteRunStore(store);
            var controlStore = new SqliteControlStore(store);

            var result = await controlStore.CommitStartAsync(
                new StartCommandPlan(StartCommandId, NewRun(), NewStartDocuments(2)),
                _ => PreconditionDecision.Accept);

            Assert.Equal(CommandCommitOutcome.Committed, result.Outcome);
            Assert.Null(result.ErrorCode);
            Assert.Equal(SeedRunId, result.Run!.Id);

            var receipt = Assert.IsType<ControlCommandReceipt>(result.Receipt);
            Assert.Equal(StartCommandId, receipt.CommandId);
            Assert.Equal(ControlCommandOperations.Start, receipt.Operation);
            Assert.Equal(SeedRunId, receipt.RunId);
            Assert.Equal(RunDesiredState.Running, receipt.DesiredState);
            Assert.Equal(RunObservedState.Running, receipt.ObservedState);
            Assert.Equal(TimeSpan.Zero, receipt.CreatedAt.Offset);
            Assert.Matches("^[0-9a-f]{64}$", receipt.Fingerprint);

            await AssertStartCommandCommittedAsync(path, runStore, controlStore, StartCommandId);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitStartAsync_RepeatedCommand_ReplaysTheDurableReceiptWithoutAdditionalWrites()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            await using var store = new SqliteStore(path);
            await store.InitializeAsync();
            var runStore = new SqliteRunStore(store);
            var controlStore = new SqliteControlStore(store);

            var first = await controlStore.CommitStartAsync(
                new StartCommandPlan(StartCommandId, NewRun(), NewStartDocuments(2)));
            var counts = CommandTableCounts(path);

            // A retry carrying a different operator note is still the same command: only the normalized
            // fingerprint is durable, and replay never consults the precondition nor writes again.
            var replay = await controlStore.CommitStartAsync(
                new StartCommandPlan(StartCommandId, NewRun(), NewStartDocuments(2), "retry C:\\operator\\notes.txt sk-live-0123456789abcdef"),
                _ => PreconditionDecision.Reject(ControlStoreErrorCodes.CommandConflict));

            Assert.Equal(CommandCommitOutcome.Replayed, replay.Outcome);
            Assert.Null(replay.ErrorCode);
            Assert.Equal(first.Receipt, replay.Receipt);
            Assert.Equal(counts, CommandTableCounts(path));
            await AssertStartCommandCommittedAsync(path, runStore, controlStore, StartCommandId);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitStartAsync_ChangedNormalizedFingerprint_ConflictsWithZeroFurtherWrites()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            await using var store = new SqliteStore(path);
            await store.InitializeAsync();
            var controlStore = new SqliteControlStore(store);

            await controlStore.CommitStartAsync(new StartCommandPlan(StartCommandId, NewRun(), NewStartDocuments(2)));
            var counts = CommandTableCounts(path);

            var conflict = await controlStore.CommitStartAsync(
                new StartCommandPlan(StartCommandId, NewRun(), NewStartDocuments(3)));

            Assert.Equal(CommandCommitOutcome.Conflict, conflict.Outcome);
            Assert.Equal("command_conflict", conflict.ErrorCode);
            Assert.Null(conflict.Receipt);
            Assert.Null(conflict.Run);
            Assert.Equal(counts, CommandTableCounts(path));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitStartAsync_PreconditionRejection_ReturnsTheStableCodeAndWritesNothing()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            await using var store = new SqliteStore(path);
            await store.InitializeAsync();
            var controlStore = new SqliteControlStore(store);

            var result = await controlStore.CommitStartAsync(
                new StartCommandPlan(StartCommandId, NewRun(), NewStartDocuments(2)),
                _ => PreconditionDecision.Reject(ControlStoreErrorCodes.CommandConflict));

            Assert.Equal(CommandCommitOutcome.Rejected, result.Outcome);
            Assert.Equal("command_conflict", result.ErrorCode);
            Assert.Null(result.Receipt);
            Assert.Null(result.Run);
            AssertCommandTablesEmpty(path);

            // The rejection vocabulary is store-owned and allowlisted: free-form text can never become a code.
            Assert.Throws<ArgumentOutOfRangeException>(() => PreconditionDecision.Reject("operator typed this"));
            Assert.True(ControlStoreErrorCodes.IsKnown(ControlStoreErrorCodes.CommandConflict));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitStartAsync_PersistsOnlyAllowlistedCommandColumnsAndNeverTheRawRequest()
    {
        const string token = "sk-live-0123456789abcdef";
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            await using var store = new SqliteStore(path);
            await store.InitializeAsync();
            var controlStore = new SqliteControlStore(store);

            var result = await controlStore.CommitStartAsync(new StartCommandPlan(
                StartCommandId,
                NewRun(),
                NewStartDocuments(2),
                $"{{\"source_folder\":\"C:\\\\operator\\\\private-folder\\\\batch.txt\",\"token\":\"{token}\"}}"));

            Assert.Equal(
                ["command_id", "normalized_fingerprint", "operation", "run_id", "desired_state", "observed_state", "created_at"],
                ReadColumns(path, "loader_command"));

            var persisted = ReadTableDump(path, "loader_command") + ReadTableDump(path, "audit_event");
            Assert.DoesNotContain(token, persisted);
            Assert.DoesNotContain("private-folder", persisted);
            Assert.DoesNotContain(token, result.Receipt!.ToString());
            Assert.Contains(result.Receipt.Fingerprint, persisted);
            Assert.True(ControlCommandOperations.IsAllowed(result.Receipt.Operation));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitStartAsync_DuplicateDocumentInPlan_RaisesAUniqueViolationAndRollsBackTheTransaction()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            await using var store = new SqliteStore(path);
            await store.InitializeAsync();
            var controlStore = new SqliteControlStore(store);

            // The plan is deliberately not pre-validated: the duplicate (run_id, candidate_id) is the only
            // honest acknowledgement-boundary fault, and it must abort the whole transaction.
            var document = NewStartDocuments(1)[0];
            var plan = new StartCommandPlan(
                StartCommandId,
                NewRun(),
                [document, document with { Id = OtherDocumentId }]);

            var exception = await Assert.ThrowsAsync<SqliteException>(() => controlStore.CommitStartAsync(plan));
            Assert.Equal(19, exception.SqliteErrorCode);
            AssertCommandTablesEmpty(path);
            Assert.Null(await controlStore.GetCommandReceiptAsync(StartCommandId));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitStartAsync_PreconditionThatThrows_RollsBackTheTransaction()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            await using var store = new SqliteStore(path);
            await store.InitializeAsync();
            var controlStore = new SqliteControlStore(store);

            await Assert.ThrowsAsync<InvalidOperationException>(() => controlStore.CommitStartAsync(
                new StartCommandPlan(StartCommandId, NewRun(), NewStartDocuments(2)),
                _ => throw new InvalidOperationException("stale batch view")));

            AssertCommandTablesEmpty(path);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // --- Desired-state intents: pause/resume (11.prev-b1b triangulation) -------

    [Fact]
    public async Task CommitDesiredStateAsync_PauseCommitsIntentAuditAndReceiptAtomically()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            await using var store = new SqliteStore(path);
            await store.InitializeAsync();
            var runStore = new SqliteRunStore(store);
            var controlStore = new SqliteControlStore(store);
            var (runId, _) = await SeedDurableWorkAsync(runStore);

            var result = await controlStore.CommitDesiredStateAsync(new DesiredStateCommandPlan(
                PauseCommandId, runId, RunDesiredState.PauseRequested, RunObservedState.Pausing));

            Assert.Equal(CommandCommitOutcome.Committed, result.Outcome);
            var receipt = Assert.IsType<ControlCommandReceipt>(result.Receipt);
            Assert.Equal(ControlCommandOperations.Pause, receipt.Operation);
            Assert.Equal(RunDesiredState.PauseRequested, receipt.DesiredState);
            Assert.Equal(RunObservedState.Pausing, receipt.ObservedState);
            Assert.Equal(PauseCommandId, (await controlStore.GetCommandReceiptAsync(PauseCommandId))!.CommandId);

            var run = await runStore.GetRunAsync(runId);
            Assert.Equal(RunDesiredState.PauseRequested, run!.DesiredState);
            Assert.Equal(RunObservedState.Pausing, run.ObservedState);

            var intent = Assert.Single(await runStore.GetAuditEventsAsync(runId), e => e.Action == "pause_requested");
            Assert.Equal(runId, intent.RunId);
            Assert.Equal("running->pausing", intent.StateTransition);
            Assert.Equal(1L, CountRows(path, "loader_command"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitDesiredStateAsync_ResumeRejectsAnIneligibleRunAndCommitsAfterTheDurableStateChanges()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            await using var store = new SqliteStore(path);
            await store.InitializeAsync();
            var runStore = new SqliteRunStore(store);
            var controlStore = new SqliteControlStore(store);
            var (runId, _) = await SeedDurableWorkAsync(runStore);

            DesiredStatePrecondition resumeWhenPaused = view => view.CurrentRun?.ObservedState == RunObservedState.Paused
                ? PreconditionDecision.Accept
                : PreconditionDecision.Reject(ControlStoreErrorCodes.CommandConflict);
            var plan = new DesiredStateCommandPlan(ResumeCommandId, runId, RunDesiredState.Running, RunObservedState.Running);

            // The run is still running: resume is an invalid transition and must not write anything.
            var ineligible = await controlStore.CommitDesiredStateAsync(plan, resumeWhenPaused);
            Assert.Equal(CommandCommitOutcome.Rejected, ineligible.Outcome);
            Assert.Equal("command_conflict", ineligible.ErrorCode);
            Assert.Equal(0L, CountRows(path, "loader_command"));
            Assert.Equal(RunObservedState.Running, (await runStore.GetRunAsync(runId))!.ObservedState);

            await runStore.SetRunObservedStateAsync(runId, RunObservedState.Paused);
            var resumed = await controlStore.CommitDesiredStateAsync(
                plan with { CommandId = ResumeCommandId },
                resumeWhenPaused);

            Assert.Equal(CommandCommitOutcome.Committed, resumed.Outcome);
            var intent = Assert.Single(await runStore.GetAuditEventsAsync(runId), e => e.Action == "resume_requested");
            Assert.Equal("paused->running", intent.StateTransition);
            Assert.Equal(RunDesiredState.Running, (await runStore.GetRunAsync(runId))!.DesiredState);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitDesiredStateAsync_UnknownRunId_IsRejectedWithoutWriting()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            await using var store = new SqliteStore(path);
            await store.InitializeAsync();
            var controlStore = new SqliteControlStore(store);

            var result = await controlStore.CommitDesiredStateAsync(new DesiredStateCommandPlan(
                PauseCommandId, Guid.NewGuid(), RunDesiredState.PauseRequested, RunObservedState.Pausing));

            Assert.Equal(CommandCommitOutcome.Rejected, result.Outcome);
            Assert.Equal("command_conflict", result.ErrorCode);
            AssertCommandTablesEmpty(path);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitDesiredStateAsync_PreconditionSeesTheRunReadInsideTheTransactionNotAStaleView()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            await using var store = new SqliteStore(path);
            await store.InitializeAsync();
            var runStore = new SqliteRunStore(store);
            var controlStore = new SqliteControlStore(store);
            var (runId, _) = await SeedDurableWorkAsync(runStore);

            var stale = await runStore.GetRunAsync(runId);
            Assert.Equal(RunObservedState.Running, stale!.ObservedState);
            await runStore.SetRunObservedStateAsync(runId, RunObservedState.Paused);

            DesiredStatePreconditionView? observed = null;
            var result = await controlStore.CommitDesiredStateAsync(
                new DesiredStateCommandPlan(ResumeCommandId, runId, RunDesiredState.Running, RunObservedState.Running),
                view => { observed = view; return PreconditionDecision.Accept; });

            Assert.Equal(CommandCommitOutcome.Committed, result.Outcome);
            Assert.Equal(RunObservedState.Paused, observed!.CurrentRun!.ObservedState);
            Assert.True(observed.RunExists);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitDesiredStateAsync_RepeatedPause_ReplaysTheReceiptWithoutAnotherAuditRow()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            await using var store = new SqliteStore(path);
            await store.InitializeAsync();
            var runStore = new SqliteRunStore(store);
            var controlStore = new SqliteControlStore(store);
            var (runId, _) = await SeedDurableWorkAsync(runStore);

            var plan = new DesiredStateCommandPlan(PauseCommandId, runId, RunDesiredState.PauseRequested, RunObservedState.Pausing);
            var first = await controlStore.CommitDesiredStateAsync(plan);
            var events = (await runStore.GetAuditEventsAsync(runId)).Count;

            // Replay never consults the precondition and never appends a second operator audit row.
            var replay = await controlStore.CommitDesiredStateAsync(
                plan with { RawRequest = "retry" },
                _ => throw new InvalidOperationException("replay must not read a precondition"));

            Assert.Equal(CommandCommitOutcome.Replayed, replay.Outcome);
            Assert.Equal(first.Receipt, replay.Receipt);
            Assert.Equal(events, (await runStore.GetAuditEventsAsync(runId)).Count);
            Assert.Equal(1L, CountRows(path, "loader_command"));
            Assert.Equal(RunObservedState.Pausing, (await runStore.GetRunAsync(runId))!.ObservedState);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitStartAsync_DifferentRunInFlight_ReportsTheDurableActiveRunToThePrecondition()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            await using var store = new SqliteStore(path);
            await store.InitializeAsync();
            var runStore = new SqliteRunStore(store);
            var controlStore = new SqliteControlStore(store);
            await SeedDurableWorkAsync(runStore);

            StartPreconditionView? observed = null;
            var result = await controlStore.CommitStartAsync(
                new StartCommandPlan(StartCommandId, NewRun() with { Id = OtherRunId }, NewStartDocuments(2)),
                view => { observed = view; return RejectWhenRunExists(view); });

            Assert.Equal(CommandCommitOutcome.Rejected, result.Outcome);
            Assert.Equal("command_conflict", result.ErrorCode);
            Assert.False(observed!.RunExists);
            Assert.True(observed.HasActiveRun);
            Assert.Equal(SeedRunId, observed.ActiveRun!.Id);

            // Active-run contention writes nothing: the seeded run is the only durable run.
            Assert.Equal(1L, CountRows(path, "run"));
            Assert.Equal(0L, CountRows(path, "loader_command"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitStartAsync_ConcurrentStartsThroughTheSingleWriter_ExactlyOneWinsAndTheLoserWritesNothing()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            await using var store = new SqliteStore(path);
            await store.InitializeAsync();
            var controlStore = new SqliteControlStore(store);

            var results = await Task.WhenAll(
                controlStore.CommitStartAsync(new StartCommandPlan(StartCommandId, NewRun(), NewStartDocuments(2)), RejectWhenRunExists),
                controlStore.CommitStartAsync(new StartCommandPlan(SecondCommandId, NewRun(), NewStartDocuments(2)), RejectWhenRunExists));

            var winner = Assert.Single(results, result => result.Outcome == CommandCommitOutcome.Committed);
            var loser = Assert.Single(results, result => result.Outcome == CommandCommitOutcome.Rejected);
            Assert.Equal("command_conflict", loser.ErrorCode);
            Assert.Equal(winner.Receipt!.CommandId, (await controlStore.GetCommandReceiptAsync(winner.Receipt.CommandId))!.CommandId);
            Assert.Equal(1L, CountRows(path, "run"));
            Assert.Equal(2L, CountRows(path, "run_document"));
            Assert.Equal(1L, CountRows(path, "audit_event"));
            Assert.Equal(1L, CountRows(path, "loader_command"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

        // --- Precondition-decision safety: no free-form rejection code can reach the store (11.prev-b1b) ---

        [Fact]
        public void PreconditionDecision_ExposesNoPublicConstructor_SoOnlyTheAllowlistedFactoriesCanReject()
        {
            // A public constructor would let any caller attach free-form text to a rejection and have the store
            // echo it as if it were a store-owned stable code.
            Assert.Empty(typeof(PreconditionDecision).GetConstructors(BindingFlags.Public | BindingFlags.Instance));

            var accepted = PreconditionDecision.Accept;
            Assert.True(accepted.Accepted);
            Assert.Null(accepted.ErrorCode);

            var rejected = PreconditionDecision.Reject(ControlStoreErrorCodes.CommandConflict);
            Assert.False(rejected.Accepted);
            Assert.Equal("command_conflict", rejected.ErrorCode);
            Assert.True(ControlStoreErrorCodes.IsKnown(rejected.ErrorCode));
            Assert.Throws<ArgumentOutOfRangeException>(() => PreconditionDecision.Reject("operator typed this"));
        }

        [Fact]
        public async Task CommitStartAsync_RejectionCarryingAForgedCode_IsRefusedAndWritesNothing()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                // Bypass regression: a decision built by reflection instead of the allowlisted factory must never
                // reach the operator surface as a code. The commit fails closed and writes nothing.
                var forged = ForgeDecision(accepted: false, "operator typed this");

                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controlStore.CommitStartAsync(
                    new StartCommandPlan(StartCommandId, NewRun(), NewStartDocuments(2)),
                    _ => forged));

                AssertCommandTablesEmpty(path);
                Assert.Null(await controlStore.GetCommandReceiptAsync(StartCommandId));
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task CommitDesiredStateAsync_RejectionWithoutAStoreOwnedCode_IsRefusedAndLeavesTheRunUntouched()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);
                var controlStore = new SqliteControlStore(store);
                var (runId, _) = await SeedDurableWorkAsync(runStore);
                var auditBefore = (await runStore.GetAuditEventsAsync(runId)).Count;

                // The zero value of the struct is always constructible and rejects with no code at all, so the
                // store itself must refuse a decision that carries no allowlisted code.
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controlStore.CommitDesiredStateAsync(
                    new DesiredStateCommandPlan(PauseCommandId, runId, RunDesiredState.PauseRequested, RunObservedState.Pausing),
                    _ => default));

                Assert.Equal(0L, CountRows(path, "loader_command"));
                Assert.Equal(auditBefore, (await runStore.GetAuditEventsAsync(runId)).Count);
                var run = await runStore.GetRunAsync(runId);
                Assert.Equal(RunDesiredState.Running, run!.DesiredState);
                Assert.Equal(RunObservedState.Running, run.ObservedState);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        // --- Coherent snapshot + inventory projection + privacy sentinel (11.prev-b2) ---

        [Fact]
        public async Task GetControlSnapshotAsync_ReadsEveryFieldCoherentlyAndGroupsDocumentCountsBySnakeCasedState()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);
                await controlStore.RecordEngineInstanceAsync("engine-1");

                // Fixture-only raw seeds: three more documents in three different durable states.
                InsertDocuments(path, SeedRunId, DocumentState.Extracting, DocumentState.RemotePending, DocumentState.RetryWait);

                var snapshot = await controlStore.GetControlSnapshotAsync(SeedRunId);

                Assert.True(snapshot.HasRun);
                Assert.Equal(SeedRunId, snapshot.RunId);
                Assert.Equal("running", snapshot.DesiredState);
                Assert.Equal("running", snapshot.ObservedState);
                Assert.Equal(new DateTimeOffset(7500, TimeSpan.Zero), snapshot.CheckpointAt);
                Assert.Null(snapshot.BlockCode);
                Assert.Equal("engine-1", snapshot.EngineInstanceId);
                Assert.Equal(1L, snapshot.EventHighWaterMark);

                // Every document is counted exactly once, under its snake-cased durable state.
                Assert.Equal(
                    ["extracting", "remote_pending", "retry_wait", "staged"],
                    snapshot.DocumentCounts.Keys.OrderBy(key => key, StringComparer.Ordinal));
                Assert.All(snapshot.DocumentCounts.Values, count => Assert.Equal(1, count));
                Assert.Equal((long)snapshot.DocumentCounts.Values.Sum(), CountRows(path, "run_document"));

                // Inventory is candidate-derived from the latest manifest: no scan totals are invented.
                Assert.Equal(SeedManifestId.ToString(), snapshot.Inventory.ManifestId);
                Assert.Equal("complete", snapshot.Inventory.Completeness);
                Assert.Equal(1, snapshot.Inventory.CandidateCount);
                Assert.Equal(1234L, snapshot.Inventory.CandidateBytes);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task GetControlSnapshotAsync_WithoutAnyRun_ReportsAnExplicitEmptyResultAndStillReportsInventory()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                // Fixture-only raw delete: the durable state holds no run at all.
                ExecuteSql(path, "DELETE FROM run_document; DELETE FROM run;");

                var snapshot = await controlStore.GetControlSnapshotAsync();

                Assert.False(snapshot.HasRun);
                Assert.Null(snapshot.RunId);
                Assert.Null(snapshot.DesiredState);
                Assert.Null(snapshot.ObservedState);
                Assert.Null(snapshot.CheckpointAt);
                Assert.Null(snapshot.BlockCode);
                Assert.Empty(snapshot.DocumentCounts);

                // An absent run is not an absent installation: persisted inventory is still reported.
                Assert.Equal(SeedManifestId.ToString(), snapshot.Inventory.ManifestId);
                Assert.Equal("complete", snapshot.Inventory.Completeness);
                Assert.Equal(1, snapshot.Inventory.CandidateCount);
                Assert.Equal(1234L, snapshot.Inventory.CandidateBytes);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task GetControlSnapshotAsync_ResolvesTheMostRecentRunWithoutARunIdAndReportsAnUnknownRunAsEmpty()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                // The seeded run started at 7000; this one starts at 8000 and holds one loaded document.
                InsertRun(path, NewerRunId, 8000, DocumentState.Loaded);

                var latest = await controlStore.GetControlSnapshotAsync();

                Assert.True(latest.HasRun);
                Assert.Equal(NewerRunId, latest.RunId);
                Assert.Equal("completed", latest.ObservedState);
                Assert.Equal(1, latest.DocumentCounts["loaded"]);

                // An explicitly requested run that does not exist is an empty result, never another run's state.
                var unknown = await controlStore.GetControlSnapshotAsync(OtherRunId);

                Assert.False(unknown.HasRun);
                Assert.Null(unknown.RunId);
                Assert.Null(unknown.DesiredState);
                Assert.Empty(unknown.DocumentCounts);
                Assert.Equal("complete", unknown.Inventory.Completeness);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Theory]
        [InlineData(0, "running", null)]
        [InlineData(1, "pausing", null)]
        [InlineData(2, "paused", null)]
        [InlineData(3, "blocked_auth", "blocked_auth")]
        [InlineData(4, "blocked_operator_action", "blocked_operator_action")]
        [InlineData(5, "completed", null)]
        public async Task GetControlSnapshotAsync_DerivesTheBlockCodeFromTheDurableObservedStateForEveryDurableBlockedState(
            int observedState, string expectedState, string? expectedBlockCode)
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);
                ExecuteSql(path, $"UPDATE run SET observed_state = {observedState};");

                var snapshot = await controlStore.GetControlSnapshotAsync(SeedRunId);

                Assert.Equal(expectedState, snapshot.ObservedState);
                // Derived from the durable observed state only: no block column exists and none was added.
                Assert.Equal(expectedBlockCode, snapshot.BlockCode);
                Assert.DoesNotContain("block", ReadColumns(path, "run"));
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Theory]
        [InlineData(null, "none", 0, 0L)]
        [InlineData(0, "incomplete", 2, 1300L)]
        [InlineData(1, "complete", 2, 1300L)]
        public async Task GetControlSnapshotAsync_ComputesInventoryCompletenessFromTheLatestManifestAndNeverCompletesAnIncompleteScan(
            int? manifestState, string expectedCompleteness, int expectedCount, long expectedBytes)
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                if (manifestState is { } state)
                {
                    SeedManifest(path, SeedManifestId, state, startTime: 2000, 1000, 300);
                }

                var snapshot = await controlStore.GetControlSnapshotAsync();

                Assert.Equal(manifestState is null ? null : SeedManifestId.ToString(), snapshot.Inventory.ManifestId);
                Assert.Equal(expectedCompleteness, snapshot.Inventory.Completeness);
                Assert.Equal(expectedCount, snapshot.Inventory.CandidateCount);
                Assert.Equal(expectedBytes, snapshot.Inventory.CandidateBytes);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task GetControlSnapshotAsync_WithSeveralManifests_ProjectsTheLatestStartTimeThenTheGreatestManifestId()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                // An older completed scan must never win over a newer, still-running scan.
                SeedManifest(path, OtherManifestId, (int)ManifestState.Complete, startTime: 2000, 1000);
                SeedManifest(path, SeedManifestId, (int)ManifestState.Scanning, startTime: 9000, 1000, 300);

                var latest = await controlStore.GetControlSnapshotAsync();

                Assert.Equal(SeedManifestId.ToString(), latest.Inventory.ManifestId);
                Assert.Equal("incomplete", latest.Inventory.Completeness);
                Assert.Equal(2, latest.Inventory.CandidateCount);
                Assert.Equal(1300L, latest.Inventory.CandidateBytes);

                // Same start_time: the deterministic tie-break wins and only its candidates are counted.
                SeedManifest(path, GreaterManifestId, (int)ManifestState.Complete, startTime: 9000, 5);

                var tied = await controlStore.GetControlSnapshotAsync();

                Assert.Equal(GreaterManifestId.ToString(), tied.Inventory.ManifestId);
                Assert.Equal("complete", tied.Inventory.Completeness);
                Assert.Equal(1, tied.Inventory.CandidateCount);
                Assert.Equal(5L, tied.Inventory.CandidateBytes);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task GetControlSnapshotAsync_OnAnEmptyStore_ReportsNoRunNoInventoryAndZeroHighWater()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                var snapshot = await controlStore.GetControlSnapshotAsync();

                Assert.False(snapshot.HasRun);
                Assert.Null(snapshot.RunId);
                Assert.Empty(snapshot.DocumentCounts);
                Assert.Null(snapshot.EngineInstanceId);
                Assert.Equal(0L, snapshot.EventHighWaterMark);
                Assert.Null(snapshot.Inventory.ManifestId);
                Assert.Equal("none", snapshot.Inventory.Completeness);
                Assert.Equal(0, snapshot.Inventory.CandidateCount);
                Assert.Equal(0L, snapshot.Inventory.CandidateBytes);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task GetControlSnapshotAsync_SerializedSnapshot_ExposesOnlyAllowlistedFieldsAndNoPathsSecretsOrSourceKeys()
        {
            const string token = "sk-live-sentinel-0123456789abcdef";
            const string privatePath = "/srv/private/corpus";
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                // Fixture-only durable payloads the projection must never carry: a secret-bearing configuration
                // snapshot and a raw audit row whose text fields hold a path and a token.
                ExecuteSql(
                    path,
                    $"UPDATE run SET configuration_snapshot = '{{\"root\":\"{privatePath}\",\"token\":\"{token}\"}}';" +
                    $"UPDATE audit_event SET action = '{token}', state_transition = '{privatePath}/locked', outcome_code = '{privatePath}', measurements = '{privatePath}';");

                using var document = JsonDocument.Parse(JsonSerializer.Serialize(await controlStore.GetControlSnapshotAsync(SeedRunId)));

                Assert.Equal(
                    ["BlockCode", "CheckpointAt", "DesiredState", "DocumentCounts", "EngineInstanceId", "EventHighWaterMark", "HasRun", "Inventory", "ObservedState", "RunId"],
                    document.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
                Assert.Equal(
                    ["CandidateBytes", "CandidateCount", "Completeness", "ManifestId"],
                    document.RootElement.GetProperty("Inventory").EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));

                var json = document.RootElement.GetRawText();
                Assert.DoesNotContain(token, json);
                Assert.DoesNotContain(privatePath, json);
                Assert.DoesNotContain("/srv/seed", json);
                Assert.DoesNotContain("source-seed", json);
                Assert.DoesNotContain("configuration_snapshot", json);
                Assert.DoesNotContain("ConfigurationSnapshot", json);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task GetControlSnapshotAsync_ConcurrentReadsDuringInterleavedWrites_NeverMixRunInventoryDocumentOrEventProjections()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                const int readers = 8;
                const int steps = 20;

                // One interleaved step advances all four projections in an index-encoded order: it publishes manifest
                // M(k) - Complete for even k, Scanning for odd k - with exactly k candidates, then commits run R(k) -
                // pause_requested/blocked_auth for even k, running otherwise - with exactly k pending documents and one
                // start_requested audit event. A read mixing two points of that order contradicts the parity, the counts,
                // or the high-water mark of the run it reports.
                async Task WriteStepAsync(int step)
                {
                    await store.CreateManifestAsync(new Manifest(
                        CohortManifestId(step),
                        1,
                        step % 2 == 0 ? ManifestState.Complete : ManifestState.Scanning,
                        new DateTimeOffset(CohortStartTicks + step, TimeSpan.Zero)));

                    for (var index = 0; index < step; index++)
                    {
                        await store.AddCandidateAsync(new Candidate(
                            Guid.NewGuid(), CohortManifestId(step), SeedRootId, $"docs/step-{step}-{index}.txt",
                            ".txt", 100, new DateTimeOffset(4000, TimeSpan.Zero), "eligible"));
                    }

                    // Document identities encode the step too: run_document_id is a primary key, so a later cohort
                    // may not reuse an earlier cohort's document ids.
                    var documents = Enumerable.Range(1, step)
                        .Select(index => new StartDocument(
                            Guid.Parse($"a{step:D3}0000-0000-4000-8000-{index:D12}"),
                            Guid.Parse($"b{step:D3}0000-0000-4000-8000-{index:D12}"),
                            $"source-{step}-{index}"))
                        .ToArray();

                    await controlStore.CommitStartAsync(new StartCommandPlan(
                        Guid.NewGuid(),
                        new Run(
                            CohortRunId(step),
                            SampleId: null,
                            CollectionId: "cohort-collection",
                            DesiredState: step % 2 == 0 ? RunDesiredState.PauseRequested : RunDesiredState.Running,
                            ObservedState: step % 2 == 0 ? RunObservedState.BlockedAuth : RunObservedState.Running,
                            EngineVersion: "0.1.0",
                            ConfigurationSnapshot: "{\"seed\":true}",
                            StartedAt: new DateTimeOffset(CohortStartTicks + step, TimeSpan.Zero)),
                        documents));
                }

                // Step 1 commits before any read starts, so every read reports a cohort run and never the seed run.
                await WriteStepAsync(1);

                // The readers keep reading until the writer finishes, so each read is taken while the writer is
                // advancing: the observations have to span several steps, and every one of them is checked against
                // the single step it reports.
                using var finished = new CancellationTokenSource();
                var reads = Enumerable.Range(0, readers).Select(_ => Task.Run(async () =>
                {
                    var seen = new List<int>();
                    while (!finished.IsCancellationRequested)
                    {
                        var snapshot = await controlStore.GetControlSnapshotAsync();
                        AssertSnapshotNamesOneCohortStep(snapshot);
                        seen.Add(CohortIndex(snapshot.RunId!.Value));
                    }

                    return seen;
                })).ToArray();

                try
                {
                    for (var step = 2; step <= steps; step++)
                    {
                        await WriteStepAsync(step);
                    }
                }
                finally
                {
                    finished.Cancel();
                }

                var observed = (await Task.WhenAll(reads))
                    .SelectMany(seen => seen)
                    .Distinct()
                    .OrderBy(step => step)
                    .ToArray();

                // Reads that all finished before the first write would prove no interleaving at all.
                Assert.True(
                    observed.Length > 1,
                    $"No read overlapped writer progress: the reads only ever observed step(s) {string.Join(",", observed)}.");

                // The settled read agrees with the durable truth every step wrote.
                var settled = await controlStore.GetControlSnapshotAsync();
                Assert.Equal(CohortRunId(steps), settled.RunId);
                Assert.Equal(steps, settled.DocumentCounts["pending"]);
                Assert.Equal(steps + 1L, settled.EventHighWaterMark);
                Assert.Equal(CohortManifestId(steps), Guid.Parse(settled.Inventory.ManifestId!));
                Assert.Equal(steps, settled.Inventory.CandidateCount);
                Assert.Equal("complete", settled.Inventory.Completeness);
                Assert.Equal(steps, CountRows(path, "loader_command"));
                Assert.Equal(steps + 1L, CountRows(path, "audit_event"));
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task GetControlSnapshotAsync_ForARunWithoutDocumentsOrCheckpoint_StaysEmptyYetDeterministicAcrossRepeatedReads()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                // A run with no documents and no checkpoint: sum(counts) == 0 is still a run, not an empty result.
                ExecuteSql(path, "DELETE FROM run_document; UPDATE run SET checkpoint_at = NULL;");

                var first = await controlStore.GetControlSnapshotAsync(SeedRunId);
                var second = await controlStore.GetControlSnapshotAsync(SeedRunId);

                Assert.True(first.HasRun);
                Assert.Equal(SeedRunId, first.RunId);
                Assert.Empty(first.DocumentCounts);
                Assert.Equal(0, first.DocumentCounts.Values.Sum());
                Assert.Null(first.CheckpointAt);

                // Two reads of an unchanged store are identical: no clock, ordering, or identity leak.
                Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        // --- Document keyset pages (11.prev-b3) ---------------------------------

        [Fact]
        public async Task GetDocumentPageAsync_PagesTheRunByCreatedAtThenDocumentIdWithoutDuplicatingOrSkippingRows()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                // Documents 1 and 2 share a created_at, so the identity is the only tie-break, and they are seeded in
                // reverse order: the page must follow the durable keyset, not the row order of the table.
                ExecuteSql(path, "DELETE FROM run_document;");
                SeedPageDocuments(
                    path, SeedRunId,
                    (9000, 3, DocumentState.Pending),
                    (8000, 2, DocumentState.Pending),
                    (8000, 1, DocumentState.Pending),
                    (10000, 4, DocumentState.Pending),
                    (11000, 5, DocumentState.Pending));

                var visited = new List<Guid>();
                DocumentPageCursor? cursor = null;
                var pages = 0;
                do
                {
                    var page = await controlStore.GetDocumentPageAsync(SeedRunId, 2, cursor);
                    Assert.Equal(ControlPageOutcome.Ok, page.Outcome);
                    visited.AddRange(page.Page!.Documents.Select(document => document.DocumentId));
                    cursor = page.Page.NextCursor;
                    pages++;
                }
                while (cursor is not null);

                Assert.Equal(3, pages);
                Assert.Equal(
                    new[] { PageDocumentId(1), PageDocumentId(2), PageDocumentId(3), PageDocumentId(4), PageDocumentId(5) },
                    visited);

                // The last full page hands out its own keyset position, and the read after it is an explicit end.
                var full = await controlStore.GetDocumentPageAsync(SeedRunId, 5);
                Assert.Equal(5, full.Page!.Documents.Count);
                var end = await controlStore.GetDocumentPageAsync(SeedRunId, 5, full.Page.NextCursor);
                Assert.Empty(end.Page!.Documents);
                Assert.Null(end.Page.NextCursor);

                // A run without documents and a run that does not exist are both empty pages, never an error.
                ExecuteSql(path, "DELETE FROM run_document;");
                Assert.Empty((await controlStore.GetDocumentPageAsync(SeedRunId, 2)).Page!.Documents);
                Assert.Equal(ControlPageOutcome.Ok, (await controlStore.GetDocumentPageAsync(OtherRunId, 2)).Outcome);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task GetDocumentPageAsync_ReportsCurrentState_SoAStateChangeBetweenTwoPagesAppearsOnTheLaterPage()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                ExecuteSql(path, "DELETE FROM run_document;");
                SeedPageDocuments(
                    path, SeedRunId,
                    (8000, 1, DocumentState.Pending),
                    (9000, 2, DocumentState.Pending),
                    (10000, 3, DocumentState.Pending),
                    (11000, 4, DocumentState.Pending));

                var first = await controlStore.GetDocumentPageAsync(SeedRunId, 2);
                Assert.Equal(
                    new[] { PageDocumentId(1), PageDocumentId(2) },
                    first.Page!.Documents.Select(document => document.DocumentId));

                // The document that belongs to the next page advances between the two reads: a current-state page
                // reports the state that is durable when the later page is read, not what the first page saw.
                ExecuteSql(
                    path,
                    $"UPDATE run_document SET state = {(int)DocumentState.Loaded}, updated_at = 12000 WHERE run_document_id = '{PageDocumentId(3)}';");

                var second = await controlStore.GetDocumentPageAsync(SeedRunId, 2, first.Page.NextCursor);
                Assert.Equal(
                    new[] { PageDocumentId(3), PageDocumentId(4) },
                    second.Page!.Documents.Select(document => document.DocumentId));
                Assert.Equal(PageDocumentId(3), second.Page.Documents[0].DocumentId);
                Assert.Equal("loaded", second.Page.Documents[0].State);
                Assert.Equal(new DateTimeOffset(12000, TimeSpan.Zero), second.Page.Documents[0].UpdatedAt);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(101)]
        public async Task GetDocumentPageAsync_RejectsANonPositiveOrOversizedLimitAsAnInvalidRequest(int limit)
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                var result = await controlStore.GetDocumentPageAsync(SeedRunId, limit);

                Assert.Equal(ControlPageOutcome.InvalidRequest, result.Outcome);
                Assert.Equal("malformed_request", result.ErrorCode);
                Assert.Null(result.Page);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task GetDocumentPageAsync_SerializedPages_ExposeOnlyAllowlistedFieldsAndNoSourceKeyHashRemoteIdOrConfiguration()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                ExecuteSql(
                    path,
                    "DELETE FROM run_document;" +
                    "UPDATE run SET configuration_snapshot = '{\"root\":\"/srv/private\",\"token\":\"sk-live-sentinel\"}';");
                SeedPageDocuments(path, SeedRunId, (8000, 1, DocumentState.Pending));
                var eventId = SeedEvent(path, SeedRunId, "staged_bytes", "512");

                var documentPage = await controlStore.GetDocumentPageAsync(SeedRunId, 100);
                // The frozen seed already holds durable event 1; the exclusive cursor after it makes the hostile
                // event the single-row window this allowlist/sentinel scan serializes.
                var eventPage = await controlStore.GetEventPageAsync(100, eventId - 1);
                var document = Assert.Single(documentPage.Page!.Documents);

                // The four per-operation counters travel as the allowlisted keys with their durable values; the
                // identity, candidate identity, state, classification and update time are the only other fields.
                Assert.Equal(
                    ControlStoreVocabulary.AttemptKeys.OrderBy(key => key, StringComparer.Ordinal),
                    document.Attempts.Keys.OrderBy(key => key, StringComparer.Ordinal));
                Assert.Equal(
                    new[] { 1, 0, 1, 0 },
                    new[] { document.Attempts["reserve"], document.Attempts["upload"], document.Attempts["commit"], document.Attempts["poll"] });
                Assert.Equal("pending", document.State);
                Assert.Equal("ok", document.Classification);

                using var documents = JsonDocument.Parse(JsonSerializer.Serialize(documentPage.Page));
                using var events = JsonDocument.Parse(JsonSerializer.Serialize(eventPage.Page));

                Assert.Equal(new[] { "Documents", "NextCursor", "RunId" }, PropertyNames(documents.RootElement));
                Assert.Equal(
                    new[] { "Attempts", "CandidateId", "Classification", "DocumentId", "State", "UpdatedAt" },
                    PropertyNames(documents.RootElement.GetProperty("Documents")[0]));
                Assert.Equal(new[] { "Events", "HighWaterMark", "NextCursor" }, PropertyNames(events.RootElement));
                Assert.Equal(
                    new[] { "Action", "AttemptNumber", "CandidateId", "EventId", "Measurements", "OutcomeCode", "RunId", "StateTransition", "Timestamp" },
                    PropertyNames(events.RootElement.GetProperty("Events")[0]));
                // The measurement crosses the boundary as a bare number, never as free text that could carry a
                // path or a configuration payload.
                Assert.Equal(
                    JsonValueKind.Number,
                    events.RootElement.GetProperty("Events")[0].GetProperty("Measurements").ValueKind);

                // The durable sentinels exist in the store and in the run's configuration snapshot, yet no page byte
                // can carry them: no projection field holds a source key, hash, remote ID, path, or secret.
                var json = documents.RootElement.GetRawText() + events.RootElement.GetRawText();
                Assert.DoesNotContain(PageSourceKeySentinel, json);
                Assert.DoesNotContain(PageHashSentinel, json);
                Assert.DoesNotContain(PageRemoteIdSentinel, json);
                Assert.DoesNotContain("/srv/private", json);
                Assert.DoesNotContain("sk-live-sentinel", json);
                Assert.DoesNotContain("source_document_key", json);
                Assert.DoesNotContain("extraction_hash", json);
                Assert.DoesNotContain("configuration_snapshot", json);
                Assert.DoesNotContain("ConfigurationSnapshot", json);
                Assert.Equal(eventId, Assert.Single(eventPage.Page!.Events).EventId);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        // --- Event pages, cursors and resync (11.prev-b3) -----------------------

        [Fact]
        public async Task GetEventPageAsync_ReadsAscendingEventsFromAnExclusiveCursorWithTheDurableHighWaterMarkAndNumericMeasurements()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                // Event 1 is the seeded 'staged' row carrying '128'; the two added events carry 512 and nothing.
                Assert.Equal(1L, (await controlStore.GetEventPageAsync(1)).Page!.Events[0].EventId);
                var second = SeedEvent(path, SeedRunId, "staged_bytes", "512");
                var third = SeedEvent(path, SeedRunId, "loaded", null);
                Assert.Equal(2L, second);
                Assert.Equal(3L, third);

                var first = await controlStore.GetEventPageAsync(2);
                Assert.Equal(ControlPageOutcome.Ok, first.Outcome);
                Assert.Equal(new[] { 1L, second }, first.Page!.Events.Select(page => page.EventId));
                Assert.Equal(3L, first.Page.HighWaterMark);
                Assert.Equal(second, first.Page.NextCursor);
                Assert.Equal("staged", first.Page.Events[0].Action);
                Assert.Equal(SeedRunId, first.Page.Events[0].RunId);
                Assert.Equal(new DateTimeOffset(5000, TimeSpan.Zero), first.Page.Events[0].Timestamp);
                Assert.Equal(128.0, first.Page.Events[0].Measurements);
                Assert.Equal(512.0, first.Page.Events[1].Measurements);

                // The cursor is exclusive, and a short page before the high-water mark ends the traversal.
                var last = await controlStore.GetEventPageAsync(2, second);
                Assert.Equal(new[] { third }, last.Page!.Events.Select(page => page.EventId));
                Assert.Null(last.Page.Events[0].Measurements);
                Assert.Null(last.Page.NextCursor);
                Assert.Equal(3L, last.Page.HighWaterMark);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task GetEventPageAsync_CapsThePageAtTheStoreMaximumAndOnlyHandsOutACursorWhenMoreRemain()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                SeedEvents(path, SeedRunId, 104, "staged_bytes"); // durable ids 2..105

                var capped = await controlStore.GetEventPageAsync(ControlStoreVocabulary.MaxPageSize);
                Assert.Equal(ControlStoreVocabulary.MaxPageSize, capped.Page!.Events.Count);
                Assert.Equal(1L, capped.Page.Events[0].EventId);
                Assert.Equal(100L, capped.Page.Events[^1].EventId);
                Assert.Equal(100L, capped.Page.NextCursor);
                Assert.Equal(105L, capped.Page.HighWaterMark);

                var oversized = await controlStore.GetEventPageAsync(ControlStoreVocabulary.MaxPageSize + 1);
                Assert.Equal(ControlPageOutcome.InvalidRequest, oversized.Outcome);
                Assert.Null(oversized.Page);

                // The final page of the window is exactly at the limit and its last event is the high-water mark:
                // nothing remains, so it offers no cursor and the read after it is an explicit empty page.
                var atLimit = await controlStore.GetEventPageAsync(5, 100);
                Assert.Equal(new[] { 101L, 102L, 103L, 104L, 105L }, atLimit.Page!.Events.Select(page => page.EventId));
                Assert.Null(atLimit.Page.NextCursor);

                var exhausted = await controlStore.GetEventPageAsync(5, 105);
                Assert.Empty(exhausted.Page!.Events);
                Assert.Null(exhausted.Page.NextCursor);
                Assert.Equal(105L, exhausted.Page.HighWaterMark);

                // A one-row page that fills its limit before the mark still hands out its exclusive cursor.
                Assert.Equal(1L, (await controlStore.GetEventPageAsync(1)).Page!.NextCursor);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task GetEventPageAsync_HonoursTheFixedThroughBoundAndTheRunFilter()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                // Event 1 belongs to the seeded run; 2 belongs to another run, 3 to no run at all, 4 to the seed run.
                var other = SeedEvent(path, OtherRunId, "loaded", "7");
                var unowned = SeedEvent(path, null, "sample_select", "42");
                var mine = SeedEvent(path, SeedRunId, "staged_bytes", "512");
                Assert.Equal(new[] { 2L, 3L, 4L }, new[] { other, unowned, mine });

                var filtered = await controlStore.GetEventPageAsync(ControlStoreVocabulary.MaxPageSize, runId: SeedRunId);
                Assert.Equal(new[] { 1L, mine }, filtered.Page!.Events.Select(page => page.EventId));
                Assert.Equal(4L, filtered.Page.HighWaterMark); // the durable mark, not the size of the filtered window

                // A full page that ends exactly on the fixed bound is the end of that window, never a silent clamp.
                var bounded = await controlStore.GetEventPageAsync(2, throughEventId: 2);
                Assert.Equal(new[] { 1L, other }, bounded.Page!.Events.Select(page => page.EventId));
                Assert.Null(bounded.Page.NextCursor);
                Assert.Equal(4L, bounded.Page.HighWaterMark);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Theory]
        [InlineData(0, 0L, null)]
        [InlineData(101, 0L, null)]
        [InlineData(1, -1L, null)]
        [InlineData(1, 2L, 1L)]
        [InlineData(1, 0L, 9L)]
        public async Task GetEventPageAsync_RejectsAnOutOfRangeRequestAsAnInvalidRequest(int limit, long afterEventId, long? throughEventId)
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                var result = await controlStore.GetEventPageAsync(limit, afterEventId, throughEventId);

                Assert.Equal(ControlPageOutcome.InvalidRequest, result.Outcome);
                Assert.Equal("malformed_request", result.ErrorCode);
                Assert.Null(result.Page);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task GetEventPageAsync_RequiresResyncWhenTheCursorIsAboveTheHighWaterMarkOrBelowTheRetentionFloor()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                SeedEvents(path, SeedRunId, 4, "staged_bytes"); // durable ids 1..5

                // Restore or rollback: the newest events vanished, so an acknowledged cursor names nothing durable.
                ExecuteSql(path, "DELETE FROM audit_event WHERE event_id > 3;");
                var restored = await controlStore.GetEventPageAsync(3, 5);
                Assert.Equal(ControlPageOutcome.ResyncRequired, restored.Outcome);
                Assert.Equal("resync_required", restored.ErrorCode);
                Assert.Null(restored.Page);

                // A cursor exactly at the high-water mark is still contiguous: an empty page, not a resync.
                var settled = await controlStore.GetEventPageAsync(3, 3);
                Assert.Equal(ControlPageOutcome.Ok, settled.Outcome);
                Assert.Empty(settled.Page!.Events);
                Assert.Equal(3L, settled.Page.HighWaterMark);

                // Retention: raw pruning raises the derived floor. A client acknowledged at 1 has an unfillable gap.
                ExecuteSql(path, "DELETE FROM audit_event WHERE event_id <= 2;");
                Assert.Equal(ControlPageOutcome.ResyncRequired, (await controlStore.GetEventPageAsync(3, 1)).Outcome);

                // The floor boundary itself (floor - 1) still reads contiguously: no false resync is reported.
                var boundary = await controlStore.GetEventPageAsync(3, 2);
                Assert.Equal(new[] { 3L }, boundary.Page!.Events.Select(page => page.EventId));

                // An emptied table is total too: cursor 0 is a valid empty window, 1 sits above the mark.
                ExecuteSql(path, "DELETE FROM audit_event;");
                var emptied = await controlStore.GetEventPageAsync(3, 0);
                Assert.Empty(emptied.Page!.Events);
                Assert.Equal(0L, emptied.Page.HighWaterMark);
                Assert.Equal(ControlPageOutcome.ResyncRequired, (await controlStore.GetEventPageAsync(3, 1)).Outcome);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task GetEventPageAsync_AppliesTheInstallationScopedRetentionFloorAndTheThroughBoundToAFilteredWindow()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                SeedEvents(path, SeedRunId, 4, "staged_bytes"); // durable ids 1..5

                // Pruning the oldest event raises the installation-scoped floor even for a run-filtered page: the
                // floor is the durable MIN over the whole audit table, so a filter cannot hide the lost row.
                ExecuteSql(path, "DELETE FROM audit_event WHERE event_id <= 1;");
                Assert.Equal(
                    ControlPageOutcome.ResyncRequired,
                    (await controlStore.GetEventPageAsync(3, 0, runId: SeedRunId)).Outcome);

                // The floor predecessor still reads contiguously through the run filter.
                var filtered = await controlStore.GetEventPageAsync(3, 1, runId: SeedRunId);
                Assert.Equal(new[] { 2L, 3L, 4L }, filtered.Page!.Events.Select(page => page.EventId));

                // A fixed bound equal to the cursor is an explicit empty window: never a resync, never a silent clamp.
                var pinned = await controlStore.GetEventPageAsync(3, 1, throughEventId: 1, runId: SeedRunId);
                Assert.Equal(ControlPageOutcome.Ok, pinned.Outcome);
                Assert.Empty(pinned.Page!.Events);
                Assert.Null(pinned.Page.NextCursor);
                Assert.Equal(5L, pinned.Page.HighWaterMark);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task GetEventPageAsync_ServesTheSameDurableCursorAfterAReopen_SoIdsAreNotPerProcessSequences()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                long acknowledged;
                await using (var store = new SqliteStore(path))
                {
                    await store.InitializeAsync();
                    var controlStore = new SqliteControlStore(store);
                    acknowledged = (await controlStore.GetEventPageAsync(1)).Page!.Events[0].EventId;
                    Assert.Equal(1L, acknowledged);
                }

                // A new process records a new engine instance. The event cursor is installation-scoped and durable,
                // so it neither restarts at one nor repeats an already acknowledged event.
                await using (var reopened = new SqliteStore(path))
                {
                    await reopened.InitializeAsync();
                    var controlStore = new SqliteControlStore(reopened);
                    await controlStore.RecordEngineInstanceAsync("i2");

                    Assert.Empty((await controlStore.GetEventPageAsync(1, acknowledged)).Page!.Events);
                    var recorded = SeedEvent(path, SeedRunId, "loaded", null);
                    Assert.Equal(2L, recorded);
                    var continuation = await controlStore.GetEventPageAsync(1, acknowledged);
                    Assert.Equal(new[] { 2L }, continuation.Page!.Events.Select(page => page.EventId));
                    Assert.Equal(2L, continuation.Page.HighWaterMark);
                }
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Theory]
        [InlineData("{\"root\":\"/srv/private\"}")]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("1e400")]
        [InlineData("512abc")]
        public async Task GetEventPageAsync_RejectsANonNumericMeasurementAtThePageBoundary(string measurements)
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                var hostile = SeedEvent(path, SeedRunId, "loaded", measurements);
                Assert.Equal(2L, hostile);

                var thrown = await Assert.ThrowsAsync<ControlMeasurementBoundaryException>(
                    () => controlStore.GetEventPageAsync(1, hostile - 1));

                Assert.Equal(hostile, thrown.EventId);
                Assert.DoesNotContain(measurements, thrown.Message);
                Assert.DoesNotContain("private", thrown.Message);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task GetEventPageAsync_RejectsANonNumericMeasurementOnALaterRowWithoutReturningAPartialPage()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                // The hostile payload sits on the second row of a two-row page: the whole page must fail closed
                // rather than hand back the numeric row and silently drop the rest of the window.
                var numeric = SeedEvent(path, SeedRunId, "loaded", "7");
                var hostile = SeedEvent(path, SeedRunId, "staged_bytes", "{\"root\":\"/srv/private\"}");
                Assert.Equal(new[] { 2L, 3L }, new[] { numeric, hostile });

                var thrown = await Assert.ThrowsAsync<ControlMeasurementBoundaryException>(
() => controlStore.GetEventPageAsync(2, numeric - 1));
                Assert.Equal(hostile, thrown.EventId);

                // Fail-closed is not a poisoned writer: the transaction unwound, so the very next read is honest
                // and resumes exactly at the acknowledged cursor.
                ExecuteSql(path, $"DELETE FROM audit_event WHERE event_id = {hostile};");
                var recovered = await controlStore.GetEventPageAsync(2, numeric - 1);
                Assert.Equal(ControlPageOutcome.Ok, recovered.Outcome);
                Assert.Equal(new[] { numeric }, recovered.Page!.Events.Select(page => page.EventId));
                Assert.Equal(numeric, recovered.Page.HighWaterMark);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task GetEventPageAsync_RequiresResyncBelowTheRetentionFloorEvenWhenTheThroughBoundStaysValid()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                SeedEvents(path, SeedRunId, 4, "staged_bytes"); // durable ids 1..5
                ExecuteSql(path, "DELETE FROM audit_event WHERE event_id <= 2;"); // the derived floor rises to 3

                // A well-formed bound never masks a lost row: a cursor below the floor is still a resync, and it
                // carries no page at all rather than a silently shortened window.
                var stale = await controlStore.GetEventPageAsync(100, 0, throughEventId: 5);
                Assert.Equal(ControlPageOutcome.ResyncRequired, stale.Outcome);
                Assert.Equal("resync_required", stale.ErrorCode);
                Assert.Null(stale.Page);

                // The floor predecessor still reads the surviving bounded window, so resync is reported only where
                // a row really vanished and never as a blanket rejection of a legitimate request.
                var boundary = await controlStore.GetEventPageAsync(100, 2, throughEventId: 5);
                Assert.Equal(ControlPageOutcome.Ok, boundary.Outcome);
                Assert.Equal(new[] { 3L, 4L, 5L }, boundary.Page!.Events.Select(page => page.EventId));
                Assert.Null(boundary.Page.NextCursor);
                Assert.Equal(5L, boundary.Page.HighWaterMark);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        [Fact]
        public async Task GetDocumentPageAsync_SerializedNextCursor_ExposesOnlyTheTypedKeysetPosition()
        {
            var directory = NewDirectory();
            try
            {
                var path = DatabasePath(directory);
                SeedFrozenV3Database(path);

                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                ExecuteSql(path, "DELETE FROM run_document;");
                SeedPageDocuments(path, SeedRunId, (8000, 1, DocumentState.Pending), (9000, 2, DocumentState.Loaded));

                var page = await controlStore.GetDocumentPageAsync(SeedRunId, 1);
                var cursor = Assert.IsType<DocumentPageCursor>(page.Page!.NextCursor);

                // A full page hands out its keyset position as the two typed durable fields only: the position can
                // never widen into a carrier for a path, a source key, a hash, or a configuration snapshot.
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(page.Page));
                var serialized = json.RootElement.GetProperty("NextCursor");
                Assert.Equal(new[] { "CreatedAt", "RunDocumentId" }, PropertyNames(serialized));
                Assert.Equal(cursor.RunDocumentId, serialized.GetProperty("RunDocumentId").GetGuid());
                Assert.Equal(cursor.CreatedAt, serialized.GetProperty("CreatedAt").GetDateTimeOffset());
                Assert.Equal(new DateTimeOffset(8000, TimeSpan.Zero), cursor.CreatedAt);
                Assert.Equal(PageDocumentId(1), cursor.RunDocumentId);

                var text = json.RootElement.GetRawText();
                Assert.DoesNotContain(PageSourceKeySentinel, text);
                Assert.DoesNotContain(PageHashSentinel, text);
                Assert.DoesNotContain(PageRemoteIdSentinel, text);
            }
            finally
            {
                DeleteDirectory(directory);
            }
        }

        // --- Helpers ------------------------------------------------------------

        /// <summary>
        /// Builds the typed decision through its non-public constructor: the only remaining seam a caller could
        /// try to force. Forging through it proves the store's own guard, not the compiler's.
        /// </summary>
        private static PreconditionDecision ForgeDecision(bool accepted, string? errorCode)
        {
            var constructor = typeof(PreconditionDecision).GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                [typeof(bool), typeof(string)],
                modifiers: null);

            return constructor is null
                ? throw new InvalidOperationException("PreconditionDecision has no (bool, string?) constructor left to forge.")
                : (PreconditionDecision)constructor.Invoke([accepted, errorCode]);
        }

        private static async Task<(Guid RunId, Guid DocumentId)> SeedDurableWorkAsync(SqliteRunStore runStore)
    {
        var run = NewRun();
        await runStore.CreateRunAsync(run);

        var document = new RunDocument(
            SeedDocumentId,
            run.Id,
            SeedCandidateId,
            "source-seed",
            DocumentState.Pending,
            ReserveAttempts: 2,
            UploadAttempts: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        await runStore.AddRunDocumentAsync(document);
        await runStore.SaveAsync(
            document with { State = DocumentState.Staged, NormalizedTextHash = "sha256-seed", UpdatedAt = DateTimeOffset.UtcNow },
            action: "staged",
            outcomeCode: null,
            attemptNumber: 0,
            measurements: "128");

        return (run.Id, document.Id);
    }

    private static readonly Guid StartCommandId = Guid.Parse("90000000-0000-4000-8000-000000000001");
    private static readonly Guid SecondCommandId = Guid.Parse("90000000-0000-4000-8000-000000000002");
    private static readonly Guid PauseCommandId = Guid.Parse("90000000-0000-4000-8000-000000000003");
    private static readonly Guid ResumeCommandId = Guid.Parse("90000000-0000-4000-8000-000000000004");
    private static readonly Guid OtherDocumentId = Guid.Parse("a0000000-0000-4000-8000-000000000099");
    private static readonly Guid OtherRunId = Guid.Parse("55555555-5555-4555-8555-555555555590");

    private static PreconditionDecision RejectWhenRunExists(StartPreconditionView view) =>
        view.RunExists || view.HasActiveRun
            ? PreconditionDecision.Reject(ControlStoreErrorCodes.CommandConflict)
            : PreconditionDecision.Accept;

    private static StartDocument[] NewStartDocuments(int count) =>
        [.. Enumerable.Range(1, count).Select(index => new StartDocument(
            Guid.Parse($"a0000000-0000-4000-8000-{index:D12}"),
            Guid.Parse($"b0000000-0000-4000-8000-{index:D12}"),
            $"source-{index}"))];

    /// <summary>
    /// The index-encoded identity of the concurrency cohort runs and manifests: the step a read reports is
    /// recovered from the id it reported, so a mixed read can be checked against the step it actually names.
    /// </summary>
    private const long CohortStartTicks = 100000;

    private static Guid CohortRunId(int step) => Guid.Parse($"c0000000-0000-4000-8000-{step:D12}");

    private static Guid CohortManifestId(int step) => Guid.Parse($"d0000000-0000-4000-8000-{step:D12}");

        private static int CohortIndex(Guid id) => int.Parse(id.ToString()[^12..], CultureInfo.InvariantCulture);

        /// <summary>
        /// Asserts that one snapshot's run state, document counts, inventory projection and event high-water mark all
        /// belong to the same cohort step: a read that spans two of the writer's steps contradicts itself.
        /// </summary>
        private static void AssertSnapshotNamesOneCohortStep(ControlSnapshot snapshot)
        {
            Assert.True(snapshot.HasRun);
            var step = CohortIndex(snapshot.RunId!.Value);

            // Run state, document counts and the event high-water mark belong to that one committed run.
            Assert.Equal(step % 2 == 0 ? "pause_requested" : "running", snapshot.DesiredState);
            Assert.Equal(step % 2 == 0 ? "blocked_auth" : "running", snapshot.ObservedState);
            Assert.Equal(step % 2 == 0 ? "blocked_auth" : null, snapshot.BlockCode);
            Assert.Null(snapshot.CheckpointAt);
            Assert.Equal(step, snapshot.DocumentCounts["pending"]);
            Assert.Equal(step, snapshot.DocumentCounts.Values.Sum());
            Assert.Equal(step + 1L, snapshot.EventHighWaterMark);

            // The inventory is that step's own published scan, or the next step's scan still being published: a
            // lagging manifest, or a completeness/count pair taken from another scan, is a mixed read.
            var inventoryStep = CohortIndex(Guid.Parse(snapshot.Inventory.ManifestId!));
            Assert.InRange(inventoryStep, step, step + 1);
            Assert.InRange(snapshot.Inventory.CandidateCount, inventoryStep == step ? step : 0, inventoryStep);
            Assert.Equal(snapshot.Inventory.CandidateCount * 100L, snapshot.Inventory.CandidateBytes);
            Assert.Equal(inventoryStep % 2 == 0 ? "complete" : "incomplete", snapshot.Inventory.Completeness);
        }

    private static string CommandTableCounts(string path) => string.Join(
        ",",
        new[] { "run", "run_document", "audit_event", "loader_command" }.Select(table => $"{table}={CountRows(path, table)}"));

    private static void AssertCommandTablesEmpty(string path)
    {
        Assert.Equal(0L, CountRows(path, "run"));
        Assert.Equal(0L, CountRows(path, "run_document"));
        Assert.Equal(0L, CountRows(path, "audit_event"));
        Assert.Equal(0L, CountRows(path, "loader_command"));
    }

    private static string[] ReadColumns(string path, string table)
    {
        using var connection = OpenConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
        using var reader = command.ExecuteReader();

        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return [.. names];
    }

    private static async Task AssertStartCommandCommittedAsync(
        string path,
        SqliteRunStore runStore,
        SqliteControlStore controlStore,
        Guid commandId)
    {
        var run = await runStore.GetRunAsync(SeedRunId);
        Assert.Equal(RunObservedState.Running, run!.ObservedState);
        Assert.Equal(RunDesiredState.Running, run.DesiredState);

        var documents = await runStore.GetRunDocumentsAsync(SeedRunId);
        Assert.Equal(2, documents.Count);
        Assert.All(documents, document =>
        {
            Assert.Equal(DocumentState.Pending, document.State);
            Assert.Equal(0, document.ReserveAttempts + document.UploadAttempts + document.CommitAttempts + document.PollAttempts);
        });

        var audit = Assert.Single(await runStore.GetAuditEventsAsync(SeedRunId));
        Assert.Equal("start_requested", audit.Action);
        Assert.Null(audit.CandidateId);
        Assert.Null(audit.StateTransition);
        Assert.Equal(1L, CountRows(path, "loader_command"));

        var durable = await controlStore.GetCommandReceiptAsync(commandId);
        Assert.Equal(commandId, durable!.CommandId);
        Assert.Equal(SeedRunId, durable.RunId);
    }

    private static Run NewRun()
        => new(
            SeedRunId,
            SampleId: null,
            CollectionId: "seed-collection",
            DesiredState: RunDesiredState.Running,
            ObservedState: RunObservedState.Running,
            EngineVersion: "0.1.0",
            ConfigurationSnapshot: "{\"seed\":true}",
            StartedAt: DateTimeOffset.UtcNow);

    private static async Task SeedSampleAndBenchmarkTablesAsync(string path)
    {
        await using (var sampleStore = new SampleSetStore(path))
        {
            await sampleStore.InitializeAsync();
            await sampleStore.SaveAsync(
                new SampleSet(SeedSampleSetId, SeedManifestId, "v1", "xoshiro", 42, 30, 1, "95/10", GatePassed: true, Representative: true, DateTimeOffset.UtcNow, "{\"seed\":true}"),
                [new SampleMember(SeedSampleSetId, SeedCandidateId, "txt|small", "fp-seed", 1)]);
        }

        await using (var benchmarkStore = new BenchmarkObservationStore(path))
        {
            await benchmarkStore.InitializeAsync();
            await benchmarkStore.SaveObservationAsync(new BenchmarkObservation(
                SeedObservationId,
                SeedSampleSetId,
                SeedCandidateId,
                "txt|small",
                DateTimeOffset.UtcNow,
                DiscoveryDuration: TimeSpan.FromMilliseconds(10),
                SnapshotDuration: TimeSpan.FromMilliseconds(20),
                ExtractionDuration: TimeSpan.FromMilliseconds(30),
                StagingDuration: TimeSpan.FromMilliseconds(40),
                SourceBytes: 1234,
                NormalizedTextBytes: 900,
                Outcome: BenchmarkOutcome.Completed,
                ErrorCode: null,
                Resource: new ResourceSnapshot(12.5, 1048576, 4096, 0, TimeSpan.FromSeconds(1))));
        }
    }

    private static void SeedFrozenV3Database(string path)
    {
        using var connection = OpenConnection(path);

        // The frozen DDL is the pre-test fixture: it never reads the migration code under test.
        var statements = FrozenSchemaV3
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        statements.Add($"INSERT INTO loader_installation (installation_id, schema_version, created_at) VALUES ('{SeedInstallationId}', 3, 1000);");
        statements.Add($"INSERT INTO source_root (root_id, label, canonical_path, created_at, reparse_traversal_disabled) VALUES ('{SeedRootId}', 'seed-docs', '/srv/seed', 1000, 1);");
        statements.Add($"INSERT INTO manifest (manifest_id, scan_version, state, start_time, end_time) VALUES ('{SeedManifestId}', 1, 1, 2000, 3000);");
        statements.Add($"INSERT INTO candidate (candidate_id, manifest_id, root_id, relative_path, extension, byte_size, last_write_time, eligibility_code, discovery_error_code, metadata_fingerprint) VALUES ('{SeedCandidateId}', '{SeedManifestId}', '{SeedRootId}', 'docs/a.txt', '.txt', 1234, 4000, 'eligible', NULL, 'fp-seed');");
        statements.Add($"INSERT INTO audit_event (utc_timestamp, run_id, candidate_id, action, state_transition, outcome_code, attempt_number, measurements) VALUES (5000, '{SeedRunId}', '{SeedDocumentId}', 'staged', 'pending->staged', NULL, 0, '128');");
        statements.Add($"INSERT INTO enumeration_error (manifest_id, root_id, relative_path, error_code, utc_timestamp) VALUES ('{SeedManifestId}', '{SeedRootId}', 'locked/b.txt', 'access_denied', 6000);");
        statements.Add($"INSERT INTO run (run_id, sample_id, collection_id, desired_state, observed_state, engine_version, configuration_snapshot, started_at, ended_at, checkpoint_at) VALUES ('{SeedRunId}', NULL, 'seed-collection', 0, 0, '0.1.0', '{{\"seed\":true}}', 7000, NULL, 7500);");
        statements.Add($"INSERT INTO run_document (run_document_id, run_id, candidate_id, source_document_key, state, reserve_attempts, upload_attempts, commit_attempts, poll_attempts, extraction_hash, normalized_text_hash, remote_upload_id, remote_document_id, remote_version_id, remote_operation_id, terminal_classification, created_at, updated_at) VALUES ('{SeedDocumentId}', '{SeedRunId}', '{SeedCandidateId}', 'source-seed', 3, 2, 1, 0, 0, NULL, 'sha256-seed', NULL, NULL, NULL, NULL, NULL, 8000, 9000);");
        statements.Add("PRAGMA user_version = 3;");

        foreach (var statement in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }

    private static Dictionary<string, string> ReadSchemaObjects(string path)
    {
        using var connection = OpenConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY name;";
        using var reader = command.ExecuteReader();

        var objects = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            objects[reader.GetString(0)] = reader.GetString(1);
        }

        return objects;
    }

    private static string[] ReadV4Ddl(string path)
    {
        var objects = ReadSchemaObjects(path);
        return [objects["loader_command"], objects["loader_engine_instance"]];
    }

    private static string ReadTableDump(string path, string table)
    {
        using var connection = OpenConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} ORDER BY rowid;";
        using var reader = command.ExecuteReader();

        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[index] = reader.IsDBNull(index)
                    ? "<null>"
                    : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? "<null>";
            }

            rows.Add(string.Join("|", values));
        }

        return string.Join("\n", rows);
    }

    private static int ReadUserVersion(string path)
    {
        using var connection = OpenConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (int)(long)command.ExecuteScalar()!;
    }

    private static bool TableExists(string path, string table)
    {
        using var connection = OpenConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return (long)command.ExecuteScalar()! == 1L;
    }

    private static long CountRows(string path, string table)
    {
        using var connection = OpenConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Runs fixture-only SQL against the database under test through an independent connection.</summary>
    private static void ExecuteSql(string path, string sql)
    {
        using var connection = OpenConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

        /// <summary>The sentinels every non-allowlisted document column carries, so a leaked field is detectable.</summary>
        private const string PageSourceKeySentinel = "source-page-sentinel";
        private const string PageHashSentinel = "sha256-page-sentinel";
        private const string PageRemoteIdSentinel = "remote-page-sentinel";

        /// <summary>An index-encoded document identity whose text order is the intended keyset tie-break order.</summary>
        private static Guid PageDocumentId(int index) => Guid.Parse($"aaaa0000-0000-4000-8000-{index:D12}");

        /// <summary>
        /// Seeds a run's documents at explicit durable keyset positions. The attempt counters derive from the
        /// seeded position and every non-allowlisted column carries a sentinel.
        /// </summary>
        private static void SeedPageDocuments(string path, Guid runId, params (long CreatedAt, int Index, DocumentState State)[] documents)
            => ExecuteSql(path, string.Join(';', documents.Select((document, position) =>
                "INSERT INTO run_document (run_document_id, run_id, candidate_id, source_document_key, state, reserve_attempts, upload_attempts, commit_attempts, poll_attempts, extraction_hash, remote_upload_id, remote_document_id, terminal_classification, created_at, updated_at) " +
                $"VALUES ('{PageDocumentId(document.Index)}', '{runId}', '{Guid.NewGuid()}', '{PageSourceKeySentinel}', {(int)document.State}, " +
                $"{position + 1}, {position}, {position + 1}, {position}, '{PageHashSentinel}', '{PageRemoteIdSentinel}', '{PageRemoteIdSentinel}', 'ok', {document.CreatedAt}, {document.CreatedAt});")));

        /// <summary>Seeds one durable audit event on its own connection and returns its installation-scoped id.</summary>
        private static long SeedEvent(string path, Guid? runId, string action, string? measurements)
        {
            using var connection = OpenConnection(path);
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO audit_event (utc_timestamp, run_id, candidate_id, action, state_transition, outcome_code, attempt_number, measurements) " +
                "VALUES (5000, $run, NULL, $action, 'pending->staged', NULL, 1, $measurements); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$run", runId?.ToString() ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$action", action);
            command.Parameters.AddWithValue("$measurements", measurements ?? (object)DBNull.Value);
            return (long)command.ExecuteScalar()!;
        }

        /// <summary>Seeds a contiguous block of durable events without measurements.</summary>
        private static void SeedEvents(string path, Guid runId, int count, string action)
            => ExecuteSql(path, string.Join(';', Enumerable.Range(0, count).Select(_ =>
                $"INSERT INTO audit_event (utc_timestamp, run_id, candidate_id, action, state_transition, outcome_code, attempt_number, measurements) VALUES (5000, '{runId}', NULL, '{action}', NULL, NULL, 0, NULL);")));

        /// <summary>The serialized property names of one object, in ordinal order.</summary>
        private static IEnumerable<string> PropertyNames(JsonElement element) =>
            element.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal);

        private static void InsertDocuments(string path, Guid runId, params DocumentState[] states)
        => ExecuteSql(path, string.Join(';', states.Select((state, index) =>
            $"INSERT INTO run_document (run_document_id, run_id, candidate_id, source_document_key, state, created_at, updated_at) VALUES ('{Guid.NewGuid()}', '{runId}', '{Guid.NewGuid()}', 'source-b2-{index}', {(int)state}, 8000, 8000);")));

    private static void InsertRun(string path, Guid runId, long startedAtTicks, DocumentState documentState)
        => ExecuteSql(
            path,
            $"INSERT INTO run (run_id, sample_id, collection_id, desired_state, observed_state, engine_version, configuration_snapshot, started_at, ended_at, checkpoint_at) VALUES ('{runId}', NULL, 'seed-collection', 0, 5, '0.1.0', '{{\"seed\":true}}', {startedAtTicks}, NULL, NULL);" +
            $"INSERT INTO run_document (run_document_id, run_id, candidate_id, source_document_key, state, created_at, updated_at) VALUES ('{Guid.NewGuid()}', '{runId}', '{Guid.NewGuid()}', 'source-newer', {(int)documentState}, 8000, 8000);");

    /// <summary>Seeds one manifest with its candidates so the inventory projection has first-class fixtures.</summary>
    private static void SeedManifest(string path, Guid manifestId, int state, long startTime, params long[] candidateBytes)
    {
        var statements = new List<string>
        {
            $"INSERT OR IGNORE INTO source_root (root_id, label, canonical_path, created_at, reparse_traversal_disabled) VALUES ('{SeedRootId}', 'seed-docs', '/srv/seed', 1000, 1);",
            $"INSERT INTO manifest (manifest_id, scan_version, state, start_time, end_time) VALUES ('{manifestId}', 1, {state}, {startTime}, NULL);",
        };

        statements.AddRange(candidateBytes.Select((bytes, index) =>
            $"INSERT INTO candidate (candidate_id, manifest_id, root_id, relative_path, extension, byte_size, last_write_time, eligibility_code, discovery_error_code, metadata_fingerprint) VALUES ('{Guid.NewGuid()}', '{manifestId}', '{SeedRootId}', 'docs/{index}.txt', '.txt', {bytes}, 4000, 'eligible', NULL, NULL);"));

        ExecuteSql(path, string.Join(';', statements));
    }

    private static SqliteConnection OpenConnection(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rag-control-store-{Guid.NewGuid():N}");
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
