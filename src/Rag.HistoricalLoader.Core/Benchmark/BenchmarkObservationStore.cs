using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Rag.HistoricalLoader.Core.Benchmark;

/// <summary>
/// Owns the local <c>benchmark_observation</c> and <c>benchmark_report</c> tables on the
/// same SQLite database used by inventory and sampling, following the sample-set store's
/// idempotent CREATE TABLE pattern.
/// </summary>
public sealed class BenchmarkObservationStore : IAsyncDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS benchmark_observation (
            observation_id TEXT PRIMARY KEY,
            sample_set_id TEXT NOT NULL,
            candidate_id TEXT NOT NULL,
            stratum_key TEXT NOT NULL,
            started_at INTEGER NOT NULL,
            discovery_ms INTEGER NOT NULL,
            snapshot_ms INTEGER NOT NULL,
            extraction_ms INTEGER NOT NULL,
            staging_ms INTEGER NOT NULL,
            source_bytes INTEGER NOT NULL,
            normalized_bytes INTEGER NOT NULL,
            outcome INTEGER NOT NULL,
            error_code TEXT,
            cpu_percent REAL NOT NULL,
            working_set_bytes INTEGER NOT NULL,
            disk_io_bytes INTEGER NOT NULL,
            queue_growth INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS benchmark_report (
            sample_set_id TEXT PRIMARY KEY,
            report_json TEXT NOT NULL,
            persisted_at INTEGER NOT NULL
        );
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _databasePath;
    private SqliteConnection? _connection;

    public BenchmarkObservationStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await _connection.OpenAsync(cancellationToken);
        await NonQueryAsync("PRAGMA foreign_keys = ON;", cancellationToken);
        await NonQueryAsync(Schema, cancellationToken);
    }

    public async Task SaveObservationAsync(BenchmarkObservation observation, CancellationToken cancellationToken = default)
        => await NonQueryAsync(
            "INSERT INTO benchmark_observation (observation_id, sample_set_id, candidate_id, stratum_key, started_at, discovery_ms, snapshot_ms, extraction_ms, staging_ms, source_bytes, normalized_bytes, outcome, error_code, cpu_percent, working_set_bytes, disk_io_bytes, queue_growth) VALUES ($id, $sample, $candidate, $stratum, $started, $discovery, $snapshot, $extraction, $staging, $source, $normalized, $outcome, $error, $cpu, $working, $disk, $queue);",
            cancellationToken,
            ("$id", observation.Id.ToString()),
            ("$sample", observation.SampleSetId.ToString()),
            ("$candidate", observation.CandidateId.ToString()),
            ("$stratum", observation.StratumKey),
            ("$started", observation.StartedAt.UtcTicks),
            ("$discovery", observation.DiscoveryDuration.TotalMilliseconds),
            ("$snapshot", observation.SnapshotDuration.TotalMilliseconds),
            ("$extraction", observation.ExtractionDuration.TotalMilliseconds),
            ("$staging", observation.StagingDuration.TotalMilliseconds),
            ("$source", observation.SourceBytes),
            ("$normalized", observation.NormalizedTextBytes),
            ("$outcome", (int)observation.Outcome),
            ("$error", observation.ErrorCode ?? (object)DBNull.Value),
            ("$cpu", observation.Resource.CpuPercent),
            ("$working", observation.Resource.WorkingSetBytes),
            ("$disk", observation.Resource.DiskIoBytes),
            ("$queue", observation.Resource.QueueGrowth));

    public async Task SaveReportAsync(BenchmarkReport report, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(report, JsonOptions);
        await NonQueryAsync(
            "INSERT INTO benchmark_report (sample_set_id, report_json, persisted_at) VALUES ($sample, $json, $persisted) ON CONFLICT(sample_set_id) DO UPDATE SET report_json = $json, persisted_at = $persisted;",
            cancellationToken,
            ("$sample", report.SampleSetId.ToString()),
            ("$json", json),
            ("$persisted", DateTimeOffset.UtcNow.UtcTicks));
    }

    public async Task<BenchmarkReport?> LoadReportAsync(Guid sampleSetId, CancellationToken cancellationToken = default)
    {
        await using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT report_json FROM benchmark_report WHERE sample_set_id = $sample;";
        command.Parameters.AddWithValue("$sample", sampleSetId.ToString());
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<BenchmarkReport>(json, JsonOptions);
    }

    public async Task<int> CountObservationsAsync(Guid sampleSetId, CancellationToken cancellationToken = default)
    {
        await using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM benchmark_observation WHERE sample_set_id = $sample;";
        command.Parameters.AddWithValue("$sample", sampleSetId.ToString());
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public ValueTask DisposeAsync() => _connection?.DisposeAsync() ?? ValueTask.CompletedTask;

    private async Task<int> NonQueryAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
