using System.Text;
using Microsoft.Data.Sqlite;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Persistence;

namespace Rag.HistoricalLoader.Core.Lifecycle;

/// <summary>
/// SQLite-backed <see cref="ILifecycleStore"/>. It uses the owning <see cref="SqliteStore"/> connection
/// exclusively (no competing connection), so every read/write is serialized through that store's single
/// writer and the schema is owned by that store's versioned migration (v3 adds <c>run</c> and
/// <c>run_document</c>).
/// </summary>
public sealed class SqliteRunStore : ILifecycleStore
{
    private readonly SqliteStore _store;

    public SqliteRunStore(SqliteStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task CreateRunAsync(Run run, CancellationToken cancellationToken = default)
        => _store.RunExclusiveAsync(async (connection, ct) =>
        {
            await ExecuteAsync(
                connection,
                null,
                """
                INSERT INTO run (run_id, sample_id, collection_id, desired_state, observed_state, engine_version, configuration_snapshot, started_at, ended_at, checkpoint_at)
                VALUES ($id, $sample, $collection, $desired, $observed, $version, $config, $started, $ended, $checkpoint);
                """,
                ct,
                ("$id", run.Id.ToString()),
                ("$sample", run.SampleId?.ToString() ?? (object)DBNull.Value),
                ("$collection", run.CollectionId),
                ("$desired", (int)run.DesiredState),
                ("$observed", (int)run.ObservedState),
                ("$version", run.EngineVersion),
                ("$config", run.ConfigurationSnapshot),
                ("$started", run.StartedAt.UtcTicks),
                ("$ended", run.EndedAt?.UtcTicks ?? (object)DBNull.Value),
                ("$checkpoint", run.CheckpointAt?.UtcTicks ?? (object)DBNull.Value));
        }, cancellationToken);

    public Task<Run?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
        => _store.RunExclusiveAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT run_id, sample_id, collection_id, desired_state, observed_state, engine_version, configuration_snapshot, started_at, ended_at, checkpoint_at FROM run WHERE run_id = $id;";
            command.Parameters.AddWithValue("$id", runId.ToString());
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            return MapRun(reader);
        }, cancellationToken);

    public Task SetRunDesiredStateAsync(Guid runId, RunDesiredState desiredState, CancellationToken cancellationToken = default)
        => _store.RunExclusiveAsync(async (connection, ct) =>
        {
            await ExecuteAsync(
                connection,
                null,
                "UPDATE run SET desired_state = $desired WHERE run_id = $id;",
                ct,
                ("$desired", (int)desiredState),
                ("$id", runId.ToString()));
        }, cancellationToken);

    public Task SetRunObservedStateAsync(Guid runId, RunObservedState observedState, CancellationToken cancellationToken = default)
        => _store.RunExclusiveAsync(async (connection, ct) =>
        {
            await ExecuteAsync(
                connection,
                null,
                "UPDATE run SET observed_state = $observed WHERE run_id = $id;",
                ct,
                ("$observed", (int)observedState),
                ("$id", runId.ToString()));
        }, cancellationToken);

    public Task AddRunDocumentAsync(RunDocument document, CancellationToken cancellationToken = default)
        => _store.RunExclusiveAsync(async (connection, ct) =>
        {
            await UpsertRunDocumentAsync(connection, null, document, ct);
        }, cancellationToken);

    public Task<RunDocument?> GetRunDocumentAsync(Guid id, CancellationToken cancellationToken = default)
        => _store.RunExclusiveAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT run_document_id, run_id, candidate_id, source_document_key, state, reserve_attempts, upload_attempts, commit_attempts, poll_attempts, extraction_hash, normalized_text_hash, remote_upload_id, remote_document_id, remote_version_id, remote_operation_id, terminal_classification, created_at, updated_at FROM run_document WHERE run_document_id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString());
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            return MapRunDocument(reader);
        }, cancellationToken);

    public Task<IReadOnlyList<RunDocument>> GetRunDocumentsAsync(Guid runId, CancellationToken cancellationToken = default)
        => _store.RunExclusiveAsync(async (connection, ct) =>
        {
            var documents = new List<RunDocument>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT run_document_id, run_id, candidate_id, source_document_key, state, reserve_attempts, upload_attempts, commit_attempts, poll_attempts, extraction_hash, normalized_text_hash, remote_upload_id, remote_document_id, remote_version_id, remote_operation_id, terminal_classification, created_at, updated_at FROM run_document WHERE run_id = $run ORDER BY created_at, run_document_id;";
            command.Parameters.AddWithValue("$run", runId.ToString());
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                documents.Add(MapRunDocument(reader));
            }

            return (IReadOnlyList<RunDocument>)documents;
        }, cancellationToken);

    public Task<IReadOnlyList<DocumentClaim>> ClaimPendingAsync(Guid runId, int limit, CancellationToken cancellationToken = default)
        => _store.RunExclusiveAsync(async (connection, ct) =>
        {
            var claims = new List<DocumentClaim>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT run_id, run_document_id, candidate_id, source_document_key FROM run_document WHERE run_id = $run AND state = $pending ORDER BY created_at, run_document_id LIMIT $limit;";
            command.Parameters.AddWithValue("$run", runId.ToString());
            command.Parameters.AddWithValue("$pending", (int)DocumentState.Pending);
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                claims.Add(new DocumentClaim(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    Guid.Parse(reader.GetString(2)),
                    reader.GetString(3)));
            }

            return (IReadOnlyList<DocumentClaim>)claims;
        }, cancellationToken);

    public async Task<RunDocument> SaveAsync(
        RunDocument document,
        string action,
        string? outcomeCode,
        int attemptNumber,
        string? measurements,
        CancellationToken cancellationToken = default)
    {
        await _store.RunInTransactionAsync(async (connection, transaction, ct) =>
        {
            var previousState = await ReadDocumentStateAsync(connection, transaction, document.Id, ct);
            await UpsertRunDocumentAsync(connection, transaction, document, ct);

            var stateTransition = previousState is null
                ? null
                : $"{ToSnakeCase(previousState.Value)}->{ToSnakeCase(document.State)}";

            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO audit_event (utc_timestamp, run_id, candidate_id, action, state_transition, outcome_code, attempt_number, measurements) VALUES ($ts, $run, $candidate, $action, $transition, $outcome, $attempt, $measurements);",
                ct,
                ("$ts", DateTimeOffset.UtcNow.UtcTicks),
                ("$run", document.RunId.ToString()),
                ("$candidate", document.Id.ToString()),
                ("$action", action),
                ("$transition", stateTransition ?? (object)DBNull.Value),
                ("$outcome", outcomeCode ?? (object)DBNull.Value),
                ("$attempt", attemptNumber),
                ("$measurements", measurements ?? (object)DBNull.Value));
        }, cancellationToken);

            return document;
        }

        public Task<RunDocument> SaveStagedAsync(
            RunDocument document,
            string normalizedTextHash,
            long stagedBytes,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(normalizedTextHash);
            if (stagedBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(stagedBytes));
            }

            var staged = document with
            {
                State = DocumentState.Staged,
                NormalizedTextHash = normalizedTextHash,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            // Staged bytes are persisted in the audit `measurements` column (no schema change) so a restart can
            // reconstruct the staged watermark without re-reading content.
            return SaveAsync(staged, "staged", null, 0, stagedBytes.ToString(), cancellationToken);
        }

        public Task<RunDocument> SaveLoadedAsync(
            RunDocument document,
            string remoteOperationId,
            CancellationToken cancellationToken = default)
        {
            var loaded = document with
            {
                State = DocumentState.Loaded,
                RemoteOperationId = remoteOperationId,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            return SaveAsync(loaded, "loaded", null, 0, null, cancellationToken);
        }

        /// <summary>
        /// Action names whose audit measurement carries the durable staged byte count for a run document. The
        /// engine's own <c>staged</c> event carries no measurement, so the pipeline appends a dedicated
        /// <c>staged_bytes</c> event; rows written through <see cref="SaveStagedAsync"/> remain readable.
        /// </summary>
        private const string StagedBytesAction = "staged_bytes";

        /// <summary>
        /// Appends the durable staged-byte measurement for one run document without changing that document's
        /// state. It makes the staged capacity and the committed watermark recoverable after a forced restart;
        /// the document is located by (<paramref name="runId"/>, <paramref name="sourceDocumentKey"/>). Returns
        /// <c>false</c> when the run has no such document (nothing is written).
        /// </summary>
        public async Task<bool> RecordStagedBytesAsync(
            Guid runId,
            string sourceDocumentKey,
            long stagedBytes,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceDocumentKey);
            if (stagedBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(stagedBytes));
            }

            var recorded = false;
            await _store.RunInTransactionAsync(async (connection, transaction, ct) =>
            {
                var candidateId = await FindRunDocumentIdAsync(connection, transaction, runId, sourceDocumentKey, ct);
                if (candidateId is null)
                {
                    return;
                }

                await ExecuteAsync(
                    connection,
                    transaction,
                    "INSERT INTO audit_event (utc_timestamp, run_id, candidate_id, action, state_transition, outcome_code, attempt_number, measurements) VALUES ($ts, $run, $candidate, $action, NULL, NULL, 0, $measurements);",
                    ct,
                    ("$ts", DateTimeOffset.UtcNow.UtcTicks),
                    ("$run", runId.ToString()),
                    ("$candidate", candidateId),
                    ("$action", StagedBytesAction),
                    ("$measurements", stagedBytes.ToString()));
                recorded = true;
            }, cancellationToken);

            return recorded;
        }

        public Task<IReadOnlyList<WatermarkRow>> GetWatermarkRowsAsync(Guid runId, CancellationToken cancellationToken = default)
            => _store.RunExclusiveAsync(async (connection, ct) =>
            {
                var rows = new List<WatermarkRow>();
                await using var command = connection.CreateCommand();
                // A document participates in watermark accounting when it has a durable staged-byte
                // measurement OR a durable normalized-text hash (staged at least once): staged capacity is not
                // reclaimed until the server confirms the document, and Loaded is the committed receipt. A row
                // whose measurement never arrived still occupies its slot, with unknown bytes read as zero.
                command.CommandText = """
                    SELECT rd.source_document_key, rd.state,
                           (SELECT ae.measurements
                              FROM audit_event ae
                             WHERE ae.candidate_id = rd.run_document_id
                               AND ae.action IN ('staged', 'staged_bytes')
                               AND ae.measurements IS NOT NULL
                             ORDER BY ae.event_id DESC
                             LIMIT 1) AS staged_bytes
                      FROM run_document rd
                     WHERE rd.run_id = $run
                       AND (rd.normalized_text_hash IS NOT NULL OR EXISTS (
                            SELECT 1
                              FROM audit_event ae
                             WHERE ae.candidate_id = rd.run_document_id
                               AND ae.action IN ('staged', 'staged_bytes')
                               AND ae.measurements IS NOT NULL))
                     ORDER BY rd.created_at, rd.run_document_id;
                    """;
                command.Parameters.AddWithValue("$run", runId.ToString());
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var sourceKey = reader.GetString(0);
                    var state = (DocumentState)reader.GetInt64(1);
                    var stagedBytes = reader.IsDBNull(2)
                        ? 0L
                        : long.TryParse(reader.GetString(2), out var parsed) ? parsed : 0L;
                    rows.Add(new WatermarkRow(sourceKey, state == DocumentState.Loaded, stagedBytes));
                }

                return (IReadOnlyList<WatermarkRow>)rows;
            }, cancellationToken);

        public Task<IReadOnlyDictionary<DocumentState, int>> CountByStateAsync(Guid runId, CancellationToken cancellationToken = default)
        => _store.RunExclusiveAsync(async (connection, ct) =>
        {
            var counts = new Dictionary<DocumentState, int>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT state, COUNT(*) FROM run_document WHERE run_id = $run GROUP BY state;";
            command.Parameters.AddWithValue("$run", runId.ToString());
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                counts[(DocumentState)reader.GetInt64(0)] = (int)reader.GetInt64(1);
            }

            return (IReadOnlyDictionary<DocumentState, int>)counts;
        }, cancellationToken);

    public Task<IReadOnlyList<AuditEvent>> GetAuditEventsAsync(Guid runId, CancellationToken cancellationToken = default)
        => _store.RunExclusiveAsync(async (connection, ct) =>
        {
            var events = new List<AuditEvent>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT event_id, utc_timestamp, run_id, candidate_id, action, state_transition, outcome_code, attempt_number, measurements FROM audit_event WHERE run_id = $run ORDER BY event_id;";
            command.Parameters.AddWithValue("$run", runId.ToString());
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                events.Add(new AuditEvent(
                    reader.GetInt64(0),
                    new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                    reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }

            return (IReadOnlyList<AuditEvent>)events;
        }, cancellationToken);

        private static async Task<string?> FindRunDocumentIdAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            Guid runId,
            string sourceDocumentKey,
            CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT run_document_id FROM run_document WHERE run_id = $run AND source_document_key = $source ORDER BY created_at, run_document_id LIMIT 1;";
            command.Parameters.AddWithValue("$run", runId.ToString());
            command.Parameters.AddWithValue("$source", sourceDocumentKey);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull ? null : (string)value;
        }

        private static async Task<DocumentState?> ReadDocumentStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT state FROM run_document WHERE run_document_id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : (DocumentState)(long)value;
    }

    private static async Task UpsertRunDocumentAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RunDocument document,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO run_document (
                run_document_id, run_id, candidate_id, source_document_key, state,
                reserve_attempts, upload_attempts, commit_attempts, poll_attempts,
                extraction_hash, normalized_text_hash,
                remote_upload_id, remote_document_id, remote_version_id, remote_operation_id,
                terminal_classification, created_at, updated_at
            ) VALUES (
                $id, $run, $candidate, $source, $state,
                $reserve, $upload, $commit, $poll,
                $extraction_hash, $normalized_hash,
                $remote_upload, $remote_document, $remote_version, $remote_operation,
                $terminal, $created, $updated
            )
            ON CONFLICT(run_document_id) DO UPDATE SET
                run_id = excluded.run_id,
                candidate_id = excluded.candidate_id,
                source_document_key = excluded.source_document_key,
                state = excluded.state,
                reserve_attempts = excluded.reserve_attempts,
                upload_attempts = excluded.upload_attempts,
                commit_attempts = excluded.commit_attempts,
                poll_attempts = excluded.poll_attempts,
                extraction_hash = excluded.extraction_hash,
                normalized_text_hash = excluded.normalized_text_hash,
                remote_upload_id = excluded.remote_upload_id,
                remote_document_id = excluded.remote_document_id,
                remote_version_id = excluded.remote_version_id,
                remote_operation_id = excluded.remote_operation_id,
                terminal_classification = excluded.terminal_classification,
                created_at = excluded.created_at,
                updated_at = excluded.updated_at;
            """,
            cancellationToken,
            ("$id", document.Id.ToString()),
            ("$run", document.RunId.ToString()),
            ("$candidate", document.CandidateId.ToString()),
            ("$source", document.SourceDocumentKey),
            ("$state", (int)document.State),
            ("$reserve", document.ReserveAttempts),
            ("$upload", document.UploadAttempts),
            ("$commit", document.CommitAttempts),
            ("$poll", document.PollAttempts),
            ("$extraction_hash", document.ExtractionHash ?? (object)DBNull.Value),
            ("$normalized_hash", document.NormalizedTextHash ?? (object)DBNull.Value),
            ("$remote_upload", document.RemoteUploadId ?? (object)DBNull.Value),
            ("$remote_document", document.RemoteDocumentId ?? (object)DBNull.Value),
            ("$remote_version", document.RemoteVersionId ?? (object)DBNull.Value),
            ("$remote_operation", document.RemoteOperationId ?? (object)DBNull.Value),
            ("$terminal", document.TerminalClassification ?? (object)DBNull.Value),
            ("$created", document.CreatedAt.UtcTicks),
            ("$updated", document.UpdatedAt.UtcTicks));
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Run MapRun(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
        reader.GetString(2),
        (RunDesiredState)reader.GetInt64(3),
        (RunObservedState)reader.GetInt64(4),
        reader.GetString(5),
        reader.GetString(6),
        new DateTimeOffset(reader.GetInt64(7), TimeSpan.Zero),
        reader.IsDBNull(8) ? null : new DateTimeOffset(reader.GetInt64(8), TimeSpan.Zero),
        reader.IsDBNull(9) ? null : new DateTimeOffset(reader.GetInt64(9), TimeSpan.Zero));

    private static RunDocument MapRunDocument(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        Guid.Parse(reader.GetString(2)),
        reader.GetString(3),
        (DocumentState)reader.GetInt64(4),
        (int)reader.GetInt64(5),
        (int)reader.GetInt64(6),
        (int)reader.GetInt64(7),
        (int)reader.GetInt64(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.IsDBNull(15) ? null : reader.GetString(15),
        new DateTimeOffset(reader.GetInt64(16), TimeSpan.Zero),
        new DateTimeOffset(reader.GetInt64(17), TimeSpan.Zero));

    private static string ToSnakeCase(DocumentState state)
    {
        var name = state.ToString();
        var builder = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (char.IsUpper(character))
            {
                if (index > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// Durable staged/committed watermark projection for one run document. <see cref="Committed"/> is true only
/// when the document reached <see cref="DocumentState.Loaded"/> (a confirmed commit); <see cref="StagedBytes"/>
/// is the byte count recorded when the normalized text was staged.
/// </summary>
public sealed record WatermarkRow(string SourceDocumentKey, bool Committed, long StagedBytes);
