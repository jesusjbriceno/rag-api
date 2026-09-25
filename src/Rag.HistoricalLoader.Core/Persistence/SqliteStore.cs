using Microsoft.Data.Sqlite;
using Rag.HistoricalLoader.Core.Data;

namespace Rag.HistoricalLoader.Core.Persistence;

public sealed class SqliteIntegrityException : Exception
{
    public SqliteIntegrityException(string databasePath, Exception? innerException = null)
        : base($"SQLite integrity check failed for '{databasePath}'. Restore from the latest pre-migration backup or export the state; the store was not rebuilt.", innerException)
    {
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }
}

public sealed record EnumerationErrorRow(
    Guid ManifestId,
    Guid RootId,
    string RelativePath,
    string ErrorCode,
    DateTimeOffset UtcTimestamp);

public sealed class SqliteStore : IAsyncDisposable
{
    public const int CurrentSchemaVersion = 4;

    private const string SchemaV1 = """
        CREATE TABLE loader_installation (
            installation_id TEXT PRIMARY KEY,
            schema_version INTEGER NOT NULL,
            created_at INTEGER NOT NULL
        );

        CREATE TABLE source_root (
            root_id TEXT PRIMARY KEY,
            label TEXT NOT NULL,
            canonical_path TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            reparse_traversal_disabled INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE manifest (
            manifest_id TEXT PRIMARY KEY,
            scan_version INTEGER NOT NULL,
            state INTEGER NOT NULL,
            start_time INTEGER NOT NULL,
            end_time INTEGER
        );

        CREATE TABLE candidate (
            candidate_id TEXT PRIMARY KEY,
            manifest_id TEXT NOT NULL REFERENCES manifest(manifest_id),
            root_id TEXT NOT NULL REFERENCES source_root(root_id),
            relative_path TEXT NOT NULL,
            extension TEXT NOT NULL,
            byte_size INTEGER NOT NULL,
            last_write_time INTEGER NOT NULL,
            eligibility_code TEXT NOT NULL,
            discovery_error_code TEXT,
            metadata_fingerprint TEXT,
            UNIQUE (manifest_id, root_id, relative_path)
        );

        CREATE TABLE audit_event (
            event_id INTEGER PRIMARY KEY AUTOINCREMENT,
            utc_timestamp INTEGER NOT NULL,
            run_id TEXT,
            candidate_id TEXT,
            action TEXT NOT NULL,
            state_transition TEXT,
            outcome_code TEXT,
            attempt_number INTEGER NOT NULL DEFAULT 0,
            measurements TEXT
        );
        """;

    private const string SchemaV2 = """
        CREATE TABLE enumeration_error (
            error_id INTEGER PRIMARY KEY AUTOINCREMENT,
            manifest_id TEXT NOT NULL REFERENCES manifest(manifest_id),
            root_id TEXT NOT NULL REFERENCES source_root(root_id),
            relative_path TEXT NOT NULL,
            error_code TEXT NOT NULL,
            utc_timestamp INTEGER NOT NULL
        );

        CREATE INDEX idx_enumeration_error_manifest ON enumeration_error(manifest_id);
        """;

    private const string SchemaV3 = """
        CREATE TABLE run (
            run_id TEXT PRIMARY KEY,
            sample_id TEXT,
            collection_id TEXT NOT NULL,
            desired_state INTEGER NOT NULL,
            observed_state INTEGER NOT NULL,
            engine_version TEXT NOT NULL,
            configuration_snapshot TEXT NOT NULL,
            started_at INTEGER NOT NULL,
            ended_at INTEGER,
            checkpoint_at INTEGER
        );

        CREATE TABLE run_document (
            run_document_id TEXT PRIMARY KEY,
            run_id TEXT NOT NULL REFERENCES run(run_id),
            candidate_id TEXT NOT NULL,
            source_document_key TEXT NOT NULL,
            state INTEGER NOT NULL,
            reserve_attempts INTEGER NOT NULL DEFAULT 0,
            upload_attempts INTEGER NOT NULL DEFAULT 0,
            commit_attempts INTEGER NOT NULL DEFAULT 0,
            poll_attempts INTEGER NOT NULL DEFAULT 0,
            extraction_hash TEXT,
            normalized_text_hash TEXT,
            remote_upload_id TEXT,
            remote_document_id TEXT,
            remote_version_id TEXT,
            remote_operation_id TEXT,
            terminal_classification TEXT,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            UNIQUE (run_id, candidate_id)
        );

        CREATE INDEX idx_run_document_run_state ON run_document(run_id, state);
        """;

    private const string SchemaV4 = """
        CREATE TABLE loader_command (
        command_id TEXT PRIMARY KEY,
        normalized_fingerprint TEXT NOT NULL,
        operation TEXT NOT NULL,
        run_id TEXT,
        desired_state INTEGER,
        observed_state INTEGER,
        created_at INTEGER NOT NULL
        );

        CREATE TABLE loader_engine_instance (
        engine_instance_id TEXT PRIMARY KEY,
        recorded_at INTEGER NOT NULL
        );
        """;

    private static readonly IReadOnlyList<Migration> Migrations =
    [
        new Migration(1, [SchemaV1]),
        new Migration(2, [SchemaV2]),
        new Migration(3, [SchemaV3]),
        new Migration(4, [SchemaV4]),
    ];

    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly string _databasePath;
    private SqliteConnection? _connection;

    public SqliteStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A database path is required.", nameof(databasePath));
        }

        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var isNewDatabase = !File.Exists(_databasePath);

        _connection = new SqliteConnection(BuildConnectionString());
        await _connection.OpenAsync(cancellationToken);
        await EnsureIntegrityAsync(cancellationToken);
        await EnablePragmasAsync(cancellationToken);
        await MigrateAsync(isNewDatabase, cancellationToken);
    }

    public async Task<(string JournalMode, bool ForeignKeysEnabled, long SynchronousMode)> GetDurabilityConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var journal = (string)(await ScalarAsync("PRAGMA journal_mode;", cancellationToken))!;
        var foreignKeys = (long)(await ScalarAsync("PRAGMA foreign_keys;", cancellationToken))!;
        var synchronous = (long)(await ScalarAsync("PRAGMA synchronous;", cancellationToken))!;
        return (journal, foreignKeys == 1, synchronous);
    }

    public async Task<long> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
        => (long)(await ScalarAsync("PRAGMA user_version;", cancellationToken))!;

    public async Task<Guid> GetInstallationIdAsync(CancellationToken cancellationToken = default)
        => Guid.Parse((string)(await ScalarAsync("SELECT installation_id FROM loader_installation LIMIT 1;", cancellationToken))!);

    public async Task AddSourceRootAsync(SourceRoot root, CancellationToken cancellationToken = default)
        => await NonQueryAsync(
            "INSERT INTO source_root (root_id, label, canonical_path, created_at, reparse_traversal_disabled) VALUES ($id, $label, $path, $created, $reparse);",
            cancellationToken,
            ("$id", root.Id.ToString()),
            ("$label", root.Label),
            ("$path", root.CanonicalPath),
            ("$created", root.CreatedAt.UtcTicks),
            ("$reparse", root.ReparseTraversalDisabled ? 1 : 0));

    public async Task CreateManifestAsync(Manifest manifest, CancellationToken cancellationToken = default)
        => await NonQueryAsync(
            "INSERT INTO manifest (manifest_id, scan_version, state, start_time, end_time) VALUES ($id, $version, $state, $start, $end);",
            cancellationToken,
            ("$id", manifest.Id.ToString()),
            ("$version", manifest.ScanVersion),
            ("$state", (int)manifest.State),
            ("$start", manifest.StartTime.UtcTicks),
            ("$end", manifest.EndTime?.UtcTicks ?? (object)DBNull.Value));

    public async Task<ManifestState> GetManifestStateAsync(Guid manifestId, CancellationToken cancellationToken = default)
        => (ManifestState)(long)(await ScalarAsync("SELECT state FROM manifest WHERE manifest_id = $id;", cancellationToken, ("$id", manifestId.ToString())))!;

    public async Task<bool> CompleteManifestAsync(Guid manifestId, bool everyRootReachedTerminal, CancellationToken cancellationToken = default)
    {
        if (!everyRootReachedTerminal)
        {
            return false;
        }

        var rows = await NonQueryAsync(
            "UPDATE manifest SET state = $state, end_time = $end WHERE manifest_id = $id;",
            cancellationToken,
            ("$state", (int)ManifestState.Complete),
            ("$end", DateTimeOffset.UtcNow.UtcTicks),
            ("$id", manifestId.ToString()));

        if (rows == 0)
        {
            throw new InvalidOperationException($"Manifest '{manifestId}' does not exist.");
        }

        return true;
    }

    public async Task AddCandidateAsync(Candidate candidate, CancellationToken cancellationToken = default)
        => await NonQueryAsync(
            "INSERT INTO candidate (candidate_id, manifest_id, root_id, relative_path, extension, byte_size, last_write_time, eligibility_code, discovery_error_code, metadata_fingerprint) VALUES ($id, $manifest, $root, $path, $extension, $size, $mtime, $eligibility, $discovery, $fingerprint);",
            cancellationToken,
            ("$id", candidate.Id.ToString()),
            ("$manifest", candidate.ManifestId.ToString()),
            ("$root", candidate.RootId.ToString()),
            ("$path", candidate.RelativePath),
            ("$extension", candidate.Extension),
            ("$size", candidate.ByteSize),
            ("$mtime", candidate.LastWriteTime.UtcTicks),
            ("$eligibility", candidate.EligibilityCode),
            ("$discovery", candidate.DiscoveryErrorCode ?? (object)DBNull.Value),
            ("$fingerprint", candidate.MetadataFingerprint ?? (object)DBNull.Value));

        public async Task<int> CountCandidatesAsync(Guid manifestId, CancellationToken cancellationToken = default)
            => (int)(long)(await ScalarAsync("SELECT COUNT(*) FROM candidate WHERE manifest_id = $id;", cancellationToken, ("$id", manifestId.ToString())))!;

        public async Task AddEnumerationErrorAsync(
            Guid manifestId,
            Guid rootId,
            string relativePath,
            string errorCode,
            DateTimeOffset utcTimestamp,
            CancellationToken cancellationToken = default)
            => await NonQueryAsync(
                "INSERT INTO enumeration_error (manifest_id, root_id, relative_path, error_code, utc_timestamp) VALUES ($manifest, $root, $path, $code, $timestamp);",
                cancellationToken,
                ("$manifest", manifestId.ToString()),
                ("$root", rootId.ToString()),
                ("$path", relativePath),
                ("$code", errorCode),
                ("$timestamp", utcTimestamp.UtcTicks));

        public async Task<IReadOnlyList<EnumerationErrorRow>> GetEnumerationErrorsAsync(Guid manifestId, CancellationToken cancellationToken = default)
        {
            await _writer.WaitAsync(cancellationToken);
            try
            {
                var rows = new List<EnumerationErrorRow>();
                await using var command = _connection!.CreateCommand();
                command.CommandText = "SELECT manifest_id, root_id, relative_path, error_code, utc_timestamp FROM enumeration_error WHERE manifest_id = $manifest ORDER BY utc_timestamp, error_code, relative_path;";
                command.Parameters.AddWithValue("$manifest", manifestId.ToString());
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(new EnumerationErrorRow(
                        Guid.Parse(reader.GetString(0)),
                        Guid.Parse(reader.GetString(1)),
                        reader.GetString(2),
                        reader.GetString(3),
                        new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero)));
                }

                return rows;
            }
            finally
            {
                _writer.Release();
            }
        }

        public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        _writer.Dispose();
    }

    private async Task EnablePragmasAsync(CancellationToken cancellationToken)
    {
        await NonQueryAsync("PRAGMA journal_mode = WAL;", cancellationToken);
        await NonQueryAsync("PRAGMA foreign_keys = ON;", cancellationToken);
        await NonQueryAsync("PRAGMA synchronous = FULL;", cancellationToken);
    }

    private async Task EnsureIntegrityAsync(CancellationToken cancellationToken)
    {
        string result;
        try
        {
            result = (string)(await ScalarAsync("PRAGMA quick_check;", cancellationToken))!;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 11 /* SQLITE_CORRUPT */ or 26 /* SQLITE_NOTADB */)
        {
            throw new SqliteIntegrityException(_databasePath, exception);
        }

        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteIntegrityException(_databasePath);
        }
    }

    private async Task MigrateAsync(bool isNewDatabase, CancellationToken cancellationToken)
    {
        var current = (long)(await ScalarAsync("PRAGMA user_version;", cancellationToken))!;
        var pending = Migrations.Where(migration => migration.Version > current).OrderBy(migration => migration.Version).ToList();

        if (pending.Count > 0)
        {
            if (!isNewDatabase)
            {
                await BackupAsync(current, cancellationToken);
            }

            foreach (var migration in pending)
            {
                foreach (var statement in migration.Statements)
                {
                    await NonQueryAsync(statement, cancellationToken);
                }

                await NonQueryAsync($"PRAGMA user_version = {migration.Version};", cancellationToken);
            }
        }

        await SeedInstallationAsync(cancellationToken);
    }

        internal async Task RunExclusiveAsync(Func<SqliteConnection, CancellationToken, Task> action, CancellationToken cancellationToken)
        {
            await _writer.WaitAsync(cancellationToken);
            try
            {
                await action(_connection!, cancellationToken);
            }
            finally
            {
                _writer.Release();
            }
        }

        internal async Task<T> RunExclusiveAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
        {
            await _writer.WaitAsync(cancellationToken);
            try
            {
                return await action(_connection!, cancellationToken);
            }
            finally
            {
                _writer.Release();
            }
        }

        internal async Task RunInTransactionAsync(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> action, CancellationToken cancellationToken)
        {
            await _writer.WaitAsync(cancellationToken);
            try
            {
                await using var transaction = (SqliteTransaction)await _connection!.BeginTransactionAsync(cancellationToken);
                await action(_connection, transaction, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            finally
            {
                _writer.Release();
            }
        }

        private async Task BackupAsync(long fromVersion, CancellationToken cancellationToken)
    {
        var backupPath = $"{_databasePath}.pre-migration-v{fromVersion}.bak";

        await using var destination = new SqliteConnection($"Data Source={backupPath}");
        await destination.OpenAsync(cancellationToken);
        _connection!.BackupDatabase(destination);
    }

    private async Task SeedInstallationAsync(CancellationToken cancellationToken)
    {
        var count = (long)(await ScalarAsync("SELECT COUNT(*) FROM loader_installation;", cancellationToken))!;
        if (count == 0)
        {
            await NonQueryAsync(
                "INSERT INTO loader_installation (installation_id, schema_version, created_at) VALUES ($id, $version, $created);",
                cancellationToken,
                ("$id", Guid.NewGuid().ToString()),
                ("$version", CurrentSchemaVersion),
                ("$created", DateTimeOffset.UtcNow.UtcTicks));
        }
    }

    private string BuildConnectionString()
        => new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

    private async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await _writer.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection!.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            return await command.ExecuteScalarAsync(cancellationToken);
        }
        finally
        {
            _writer.Release();
        }
    }

    private async Task<int> NonQueryAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await _writer.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection!.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writer.Release();
        }
    }

    private sealed record Migration(int Version, string[] Statements);
}
