using Microsoft.Data.Sqlite;
using Rag.HistoricalLoader.Core.Extraction;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Core.Persistence;

namespace Rag.HistoricalLoader.IntegrationTests.Lifecycle;

/// <summary>
/// 11.prev-b4 — restart durability of the control-command receipt. A committed start command must survive a clean
/// dispose/reopen of the file-backed store: the reopened process reads the identical durable receipt and a replay of
/// the same command id appends no new run, document, audit, or command row. The store is the real SQLite store under
/// a real file path; only public store APIs and independent read-only SQL counts are used.
/// <para>
/// The second case closes the remaining B4 durability boundary: the durable terminal document rows
/// (<c>loaded</c>, <c>skipped_document_error</c>, <c>retry_exhausted_network</c>) and their per-operation attempt
/// counters, including the three-attempt ceiling, are reported exactly and identically after a clean reopen.
/// </para>
/// <para>
/// The successor cases complete the remaining B4 durability boundaries: the coherent control snapshot (document
/// counts, inventory projection, run state, engine identity and the durable event high-water mark) is identical
/// across a clean reopen, and an event cursor that names an event dropped by a restore is refused with
/// <c>resync_required</c> after the restored database reopens instead of silently skipping the audit gap.
/// </para>
/// </summary>
public sealed class ControlStorePersistenceTests
{
    private static readonly Guid StartCommandId = Guid.Parse("b4000000-0000-4000-8000-000000000001");
    private static readonly Guid RunId = Guid.Parse("b4000000-0000-4000-8000-000000000002");
    private static readonly Guid FirstDocumentId = Guid.Parse("b4000000-0000-4000-8000-000000000003");
    private static readonly Guid SecondDocumentId = Guid.Parse("b4000000-0000-4000-8000-000000000004");
    private static readonly Guid FirstCandidateId = Guid.Parse("b4000000-0000-4000-8000-000000000005");
    private static readonly Guid SecondCandidateId = Guid.Parse("b4000000-0000-4000-8000-000000000006");
    private static readonly Guid LoadedDocumentId = Guid.Parse("b4a00000-0000-4000-8000-000000000001");
    private static readonly Guid LoadedCandidateId = Guid.Parse("b4a00000-0000-4000-8000-000000000002");
    private static readonly Guid SkippedDocumentId = Guid.Parse("b4a00000-0000-4000-8000-000000000003");
    private static readonly Guid SkippedCandidateId = Guid.Parse("b4a00000-0000-4000-8000-000000000004");
    private static readonly Guid ExhaustedDocumentId = Guid.Parse("b4a00000-0000-4000-8000-000000000005");
    private static readonly Guid ExhaustedCandidateId = Guid.Parse("b4a00000-0000-4000-8000-000000000006");
    private static readonly Guid PauseCommandId = Guid.Parse("b4000000-0000-4000-8000-000000000007");
    private static readonly Guid ResumeCommandId = Guid.Parse("b4000000-0000-4000-8000-000000000008");

    /// <summary>One fixed write instant, so the durable keyset order of the three terminal rows is deterministic.</summary>
    private static readonly DateTimeOffset TerminalRowsWrittenAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    /// <summary>The exact durable row set one committed start command leaves behind.</summary>
    private const string OneStartCommandRows = "run=1,run_document=2,audit_event=1,loader_command=1";

    /// <summary>
    /// The exact durable projection of the three terminal rows, in durable keyset order: every state, every
    /// per-operation counter (two of them on the three-attempt ceiling), and every terminal classification.
    /// </summary>
    private static readonly string TerminalPageSignature = string.Join(
        ";",
        ExpectedRow(LoadedDocumentId, LoadedCandidateId, "loaded", (1, 2, AttemptCounter.MaxAttempts, 1), null),
        ExpectedRow(SkippedDocumentId, SkippedCandidateId, "skipped_document_error", (0, 0, 0, 0), ExtractionErrorCodes.ExtractorUnavailable),
        ExpectedRow(ExhaustedDocumentId, ExhaustedCandidateId, "retry_exhausted_network", (1, 1, AttemptCounter.MaxAttempts, 0), "retry_exhausted"));

    [Fact]
    public async Task CommitStartAsync_ReceiptSurvivesCleanReopen_AndReplayingTheSameCommandWritesNothing()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            ControlCommandReceipt committed;

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var result = await new SqliteControlStore(store).CommitStartAsync(NewPlan(rawRequest: null));

                Assert.Equal(CommandCommitOutcome.Committed, result.Outcome);
                committed = Assert.IsType<ControlCommandReceipt>(result.Receipt);
                Assert.Equal(StartCommandId, committed.CommandId);
            }

            // Read the durable outcome from an independent connection, after the writer was disposed.
            var rowsAfterCommit = CommandTableCounts(path);
            Assert.Equal(OneStartCommandRows, rowsAfterCommit);

            await using (var reopened = new SqliteStore(path))
            {
                await reopened.InitializeAsync();
                var controlStore = new SqliteControlStore(reopened);

                // The reopened process reads the durable receipt byte-identically: the receipt is a column row, not
                // process memory, so every allowlisted field — including the commit time and the fingerprint — matches.
                var durable = await controlStore.GetCommandReceiptAsync(StartCommandId);
                Assert.NotNull(durable);
                Assert.Equal(committed, durable);

                // Replaying the same command id and plan after the restart is replay-only. The retry carries a
                // different raw request on purpose: only the normalized fingerprint is durable, so the transport text
                // neither reaches a column nor turns the replay into a conflict or a second mutation.
                var replay = await controlStore.CommitStartAsync(
                    NewPlan(rawRequest: @"retry C:\operator\notes.txt sk-live-0123456789abcdef"));

                Assert.Equal(CommandCommitOutcome.Replayed, replay.Outcome);
                Assert.Null(replay.ErrorCode);
                Assert.Equal(committed, replay.Receipt);
                Assert.Equal(rowsAfterCommit, CommandTableCounts(path));
            }

            // Nothing was appended by the reopen or the replay: the row set is still the one start command.
            Assert.Equal(OneStartCommandRows, CommandTableCounts(path));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitStartAsync_AfterReopen_ComparesTheDurableFingerprintInsteadOfRestartingCommandState()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var result = await new SqliteControlStore(store).CommitStartAsync(NewPlan(rawRequest: null));
                Assert.Equal(CommandCommitOutcome.Committed, result.Outcome);
            }

            var rowsAfterCommit = CommandTableCounts(path);
            Assert.Equal(OneStartCommandRows, rowsAfterCommit);

            await using (var reopened = new SqliteStore(path))
            {
                await reopened.InitializeAsync();
                var controlStore = new SqliteControlStore(reopened);

                // An unused command id is still unused after the restart, so the read path never invents a receipt.
                Assert.Null(await controlStore.GetCommandReceiptAsync(Guid.NewGuid()));

                // Reusing the committed command id for a different plan is a conflict read from the durable
                // fingerprint after the restart, and it writes nothing.
                var conflicting = await controlStore.CommitStartAsync(
                    NewPlan(rawRequest: null, run: NewRun() with { CollectionId = "other-collection" }));

                Assert.Equal(CommandCommitOutcome.Conflict, conflicting.Outcome);
                Assert.Equal(ControlStoreErrorCodes.CommandConflict, conflicting.ErrorCode);
                Assert.Null(conflicting.Receipt);
                Assert.Equal(rowsAfterCommit, CommandTableCounts(path));
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task TerminalDocumentRows_AndPerOperationAttemptCounters_SurviveCleanReopen()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            var runId = RunId;

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);
                await runStore.CreateRunAsync(NewRun());

                // The engine's durable write path, one call per write site it owns: each upsert carries the terminal
                // state, the per-operation counters, and the terminal classification of exactly one document row.
                await runStore.SaveAsync(LoadedRow(runId), "loaded", outcomeCode: null, attemptNumber: 0, measurements: null);
                var skipped = SkippedRow(runId);
                await runStore.SaveAsync(skipped, "skipped", skipped.TerminalClassification, attemptNumber: 0, measurements: null);
                var exhausted = ExhaustedRow(runId);
                await runStore.SaveAsync(
                    exhausted, "retry_exhausted", exhausted.TerminalClassification, AttemptCounter.MaxAttempts, measurements: null);

                // The writer already reports exactly these rows and counters through the bounded control page.
                Assert.Equal(
                    TerminalPageSignature,
                    DescribeDocumentPage(await ReadDocumentPageAsync(new SqliteControlStore(store), runId)));
            }

            await using (var reopened = new SqliteStore(path))
            {
                await reopened.InitializeAsync();
                var rows = await ReadDocumentPageAsync(new SqliteControlStore(reopened), runId);

                // Exactly the committed rows: no terminal state, counter, or classification is invented, dropped,
                // or recomputed by the clean reopen.
                Assert.Equal(TerminalPageSignature, DescribeDocumentPage(rows));

                // The ceiling is durable per operation, not per document: the counter that reached it is still
                // exhausted in the reopened process, so a fourth dispatch for that stage stays refused.
                var committing = rows.Documents.Single(row => row.DocumentId == ExhaustedDocumentId);
                var ceiling = AttemptCounter.From(committing.Attempts["commit"]);
                Assert.Equal(AttemptCounter.MaxAttempts, ceiling.DispatchedAttempts);
                Assert.True(ceiling.IsExhausted);
                Assert.False(ceiling.CanDispatch);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ControlSnapshot_AfterCleanReopen_ReportsIdenticalCountsInventoryAndHighWaterMark()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            string before;

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                // One committed start command is the whole durable state: one run, two pending documents, one audit row.
                var result = await controlStore.CommitStartAsync(NewPlan(rawRequest: null));
                Assert.Equal(CommandCommitOutcome.Committed, result.Outcome);
                await controlStore.RecordEngineInstanceAsync("engine-before");

                var snapshot = await controlStore.GetControlSnapshotAsync(RunId);
                Assert.True(snapshot.HasRun);
                Assert.Equal(2, snapshot.DocumentCounts["pending"]);
                Assert.Equal(1L, snapshot.EventHighWaterMark);
                before = DescribeSnapshot(snapshot);
            }

            await using (var reopened = new SqliteStore(path))
            {
                await reopened.InitializeAsync();

                // The reopened process projects the same durable snapshot: every count, the inventory, the recorded
                // engine identity and the event high-water mark come from committed rows, not process memory.
                var after = DescribeSnapshot(await new SqliteControlStore(reopened).GetControlSnapshotAsync(RunId));
                Assert.Equal(before, after);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task EventCursor_AboveTheRestoredHighWaterMark_RequiresResyncAfterRestoreAndCleanReopen()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            long acknowledged;

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var controlStore = new SqliteControlStore(store);

                // Three operator commands write three durable audit rows in order: start, pause, resume.
                await controlStore.CommitStartAsync(NewPlan(rawRequest: null));
                await controlStore.CommitDesiredStateAsync(new DesiredStateCommandPlan(
                    PauseCommandId, RunId, RunDesiredState.PauseRequested, RunObservedState.Pausing));
                await controlStore.CommitDesiredStateAsync(new DesiredStateCommandPlan(
                    ResumeCommandId, RunId, RunDesiredState.Running, RunObservedState.Running));

                var page = await controlStore.GetEventPageAsync(ControlStoreVocabulary.MaxPageSize);
                Assert.Equal(ControlPageOutcome.Ok, page.Outcome);
                acknowledged = page.Page!.Events[^1].EventId;
            }

            Assert.Equal(3L, acknowledged);

            // A restore from a backup drops the newest events after the client acknowledged them.
            ExecuteSql(path, "DELETE FROM audit_event WHERE event_id > 2;");
            Assert.Equal(2L, CountRows(path, "audit_event"));

            await using (var reopened = new SqliteStore(path))
            {
                await reopened.InitializeAsync();
                var controlStore = new SqliteControlStore(reopened);

                // The acknowledged cursor names an event the restored database no longer holds. The gap is unfillable,
                // so the reopened process must resync instead of silently skipping the missing audit rows.
                var restored = await controlStore.GetEventPageAsync(3, acknowledged);
                Assert.Equal(ControlPageOutcome.ResyncRequired, restored.Outcome);
                Assert.Equal(ControlPageErrorCodes.ResyncRequired, restored.ErrorCode);
                Assert.Null(restored.Page);

                // The restored high-water mark itself is still contiguous: an empty page, never a resync.
                var settled = await controlStore.GetEventPageAsync(3, acknowledged - 1);
                Assert.Equal(ControlPageOutcome.Ok, settled.Outcome);
                Assert.Empty(settled.Page!.Events);
                Assert.Equal(acknowledged - 1, settled.Page.HighWaterMark);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // --- Fixtures -----------------------------------------------------------

    /// <summary>One document that reached the loaded terminal outcome through every network operation.</summary>
    private static RunDocument LoadedRow(Guid runId) => WithDispatches(
        TerminalRow(runId, LoadedDocumentId, LoadedCandidateId, DocumentState.Loaded),
        (DocumentState.Reserving, 1),
        (DocumentState.Uploading, 2),
        (DocumentState.Committing, AttemptCounter.MaxAttempts),
        (DocumentState.RemotePending, 1));

    /// <summary>One document skipped by the extraction stage, which never dispatched a network operation.</summary>
    private static RunDocument SkippedRow(Guid runId) =>
        TerminalRow(runId, SkippedDocumentId, SkippedCandidateId, DocumentState.SkippedDocumentError) with
        {
            TerminalClassification = ExtractionErrorCodes.ExtractorUnavailable,
        };

    /// <summary>One document whose commit operation exhausted the ceiling and ended the network path.</summary>
    private static RunDocument ExhaustedRow(Guid runId) => WithDispatches(
        TerminalRow(runId, ExhaustedDocumentId, ExhaustedCandidateId, DocumentState.RetryExhaustedNetwork) with
        {
            TerminalClassification = "retry_exhausted",
        },
        (DocumentState.Reserving, 1),
        (DocumentState.Uploading, 1),
        (DocumentState.Committing, AttemptCounter.MaxAttempts));

    private static RunDocument TerminalRow(Guid runId, Guid documentId, Guid candidateId, DocumentState state) => new(
        documentId,
        runId,
        candidateId,
        SourceDocumentKey: $"source-{documentId:N}",
        State: state,
        CreatedAt: TerminalRowsWrittenAt,
        UpdatedAt: TerminalRowsWrittenAt);

    /// <summary>
    /// Applies per-operation dispatch counts through the production counter, so a count past
    /// <see cref="AttemptCounter.MaxAttempts"/> cannot be seeded: the ceiling itself refuses the fourth dispatch.
    /// </summary>
    private static RunDocument WithDispatches(RunDocument document, params (DocumentState Stage, int Dispatches)[] counters)
    {
        foreach (var (stage, dispatches) in counters)
        {
            var counter = AttemptCounter.Fresh;
            for (var index = 0; index < dispatches; index++)
            {
                counter = counter.RecordDispatch();
            }

            document = document.WithAttempts(stage, counter);
        }

        return document;
    }

    private static async Task<DocumentPage> ReadDocumentPageAsync(SqliteControlStore controlStore, Guid runId)
    {
        var result = await controlStore.GetDocumentPageAsync(runId, limit: ControlStoreVocabulary.MaxPageSize);
        Assert.Equal(ControlPageOutcome.Ok, result.Outcome);
        return Assert.IsType<DocumentPage>(result.Page);
    }

    private static string DescribeDocumentPage(DocumentPage page)
        => string.Join(";", page.Documents.Select(CanonicalRow));

    private static string CanonicalRow(DocumentPageRow row) => string.Join(
        "|",
        row.DocumentId,
        row.CandidateId,
        row.State,
        string.Join(",", ControlStoreVocabulary.AttemptKeys.Select(key => $"{key}={row.Attempts[key]}")),
        row.Classification ?? "none");

    /// <summary>Builds the expected row through the same projection, so the assertion cannot drift on formatting.</summary>
    private static string ExpectedRow(
        Guid documentId,
        Guid candidateId,
        string state,
        (int Reserve, int Upload, int Commit, int Poll) attempts,
        string? classification)
        => CanonicalRow(new DocumentPageRow(
            documentId,
            candidateId,
            state,
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [ControlStoreVocabulary.AttemptKeys[0]] = attempts.Reserve,
                [ControlStoreVocabulary.AttemptKeys[1]] = attempts.Upload,
                [ControlStoreVocabulary.AttemptKeys[2]] = attempts.Commit,
                [ControlStoreVocabulary.AttemptKeys[3]] = attempts.Poll,
            },
            classification,
            UpdatedAt: default));

    private static Run NewRun() => new(
        RunId,
        SampleId: null,
        CollectionId: "legacy",
        DesiredState: RunDesiredState.Running,
        ObservedState: RunObservedState.Running,
        EngineVersion: "0.1.0",
        ConfigurationSnapshot: "{}",
        StartedAt: DateTimeOffset.UtcNow);

    private static StartCommandPlan NewPlan(string? rawRequest, Run? run = null) => new(
        StartCommandId,
        run ?? NewRun(),
        [
            new StartDocument(FirstDocumentId, FirstCandidateId, "source-first"),
            new StartDocument(SecondDocumentId, SecondCandidateId, "source-second"),
        ],
        rawRequest);

    /// <summary>
    /// The allowlisted fields of one snapshot as a stable signature, so a clean reopen must reproduce every count,
    /// run state, identity, inventory projection and the durable event high-water mark exactly.
    /// </summary>
    private static string DescribeSnapshot(ControlSnapshot snapshot)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("hasRun=").Append(snapshot.HasRun);
        builder.Append(";run=").Append(snapshot.RunId);
        builder.Append(";desired=").Append(OrNone(snapshot.DesiredState));
        builder.Append(";observed=").Append(OrNone(snapshot.ObservedState));
        builder.Append(";block=").Append(OrNone(snapshot.BlockCode));
        builder.Append(";checkpoint=").Append(OrNone(snapshot.CheckpointAt?.UtcTicks));
        builder.Append(";engine=").Append(OrNone(snapshot.EngineInstanceId));
        builder.Append(";highWater=").Append(snapshot.EventHighWaterMark);
        builder.Append(";manifest=").Append(OrNone(snapshot.Inventory.ManifestId));
        builder.Append(";completeness=").Append(snapshot.Inventory.Completeness);
        builder.Append(";candidates=").Append(snapshot.Inventory.CandidateCount);
        builder.Append(";bytes=").Append(snapshot.Inventory.CandidateBytes);
        foreach (var pair in snapshot.DocumentCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append(";count=").Append(pair.Key).Append('=').Append(pair.Value);
        }

        return builder.ToString();
    }

    private static string OrNone(string? value) => value ?? "none";

    private static string OrNone(long? value) => value?.ToString() ?? "none";

    /// <summary>
    /// The four durable composites a start command writes, counted from an independent connection so the assertion
    /// reads committed rows rather than store state.
    /// </summary>
    private static string CommandTableCounts(string path) => string.Join(
        ",",
        new[] { "run", "run_document", "audit_event", "loader_command" }
            .Select(table => $"{table}={CountRows(path, table)}"));

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
        var directory = Path.Combine(Path.GetTempPath(), $"rag-control-store-reopen-{Guid.NewGuid():N}");
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
