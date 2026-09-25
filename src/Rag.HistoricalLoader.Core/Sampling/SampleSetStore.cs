using Microsoft.Data.Sqlite;
using Rag.HistoricalLoader.Core.Data;

namespace Rag.HistoricalLoader.Core.Sampling;

/// <summary>
/// Persists a frozen <see cref="SampleSet"/> and its selected members, and records
/// a selection audit event. It owns the <c>sample_set</c> and <c>sample_set_member</c>
/// tables on the same local SQLite database used by the inventory store.
/// </summary>
public sealed class SampleSetStore : IAsyncDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS sample_set (
            sample_set_id TEXT PRIMARY KEY,
            manifest_id TEXT NOT NULL REFERENCES manifest(manifest_id),
            algorithm_version TEXT NOT NULL,
            prng_algorithm TEXT NOT NULL,
            seed INTEGER NOT NULL,
            budget INTEGER NOT NULL,
            minimum_per_stratum INTEGER NOT NULL,
            confidence_coverage_rules TEXT NOT NULL,
            gate_passed INTEGER NOT NULL,
            representative INTEGER NOT NULL,
            created_at INTEGER NOT NULL,
            coverage_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS sample_set_member (
            sample_set_id TEXT NOT NULL REFERENCES sample_set(sample_set_id),
            candidate_id TEXT NOT NULL,
            stratum_key TEXT NOT NULL,
            metadata_fingerprint TEXT NOT NULL,
            selected_rank INTEGER NOT NULL,
            PRIMARY KEY (sample_set_id, candidate_id)
        );
        """;

    private readonly string _databasePath;
    private SqliteConnection? _connection;

    public SampleSetStore(string databasePath)
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

    public async Task SaveAsync(SampleSet sampleSet, IReadOnlyList<SampleMember> members, CancellationToken cancellationToken = default)
    {
        await using var transaction = _connection!.BeginTransaction();

        await NonQueryAsync(
            "INSERT INTO sample_set (sample_set_id, manifest_id, algorithm_version, prng_algorithm, seed, budget, minimum_per_stratum, confidence_coverage_rules, gate_passed, representative, created_at, coverage_json) VALUES ($id, $manifest, $version, $prng, $seed, $budget, $minimum, $rules, $gate, $representative, $created, $coverage);",
            cancellationToken,
            ("$id", sampleSet.Id.ToString()),
            ("$manifest", sampleSet.ManifestId.ToString()),
            ("$version", sampleSet.AlgorithmVersion),
            ("$prng", sampleSet.PrngAlgorithm),
            ("$seed", sampleSet.Seed),
            ("$budget", sampleSet.Budget),
            ("$minimum", sampleSet.MinimumPerStratum),
            ("$rules", sampleSet.ConfidenceCoverageRules),
            ("$gate", sampleSet.GatePassed ? 1 : 0),
            ("$representative", sampleSet.Representative ? 1 : 0),
            ("$created", sampleSet.CreatedAt.UtcTicks),
            ("$coverage", sampleSet.CoverageJson));

        foreach (var member in members)
        {
            await NonQueryAsync(
                "INSERT INTO sample_set_member (sample_set_id, candidate_id, stratum_key, metadata_fingerprint, selected_rank) VALUES ($sample, $candidate, $stratum, $fingerprint, $rank);",
                cancellationToken,
                ("$sample", member.SampleSetId.ToString()),
                ("$candidate", member.CandidateId.ToString()),
                ("$stratum", member.StratumKey),
                ("$fingerprint", member.MetadataFingerprint),
                ("$rank", member.SelectedRank));
        }

        await NonQueryAsync(
            "INSERT INTO audit_event (utc_timestamp, run_id, candidate_id, action, state_transition, outcome_code, attempt_number, measurements) VALUES ($ts, NULL, NULL, 'sample_select', NULL, $outcome, 0, $measurements);",
            cancellationToken,
            ("$ts", DateTimeOffset.UtcNow.UtcTicks),
            ("$outcome", sampleSet.GatePassed ? "sample_gate_passed" : "sample_gate_failed"),
            ("$measurements", sampleSet.CoverageJson));

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<(SampleSet Set, IReadOnlyList<SampleMember> Members)> LoadAsync(Guid sampleSetId, CancellationToken cancellationToken = default)
    {
        SampleSet? sampleSet = null;
        await using (var command = _connection!.CreateCommand())
        {
            command.CommandText = "SELECT sample_set_id, manifest_id, algorithm_version, prng_algorithm, seed, budget, minimum_per_stratum, confidence_coverage_rules, gate_passed, representative, created_at, coverage_json FROM sample_set WHERE sample_set_id = $id;";
            command.Parameters.AddWithValue("$id", sampleSetId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException($"Sample set '{sampleSetId}' does not exist.");
            }

            sampleSet = new SampleSet(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.GetInt64(8) == 1,
                reader.GetInt64(9) == 1,
                new DateTimeOffset(reader.GetInt64(10), TimeSpan.Zero),
                reader.GetString(11));
        }

        var members = new List<SampleMember>();
        await using (var command = _connection!.CreateCommand())
        {
            command.CommandText = "SELECT sample_set_id, candidate_id, stratum_key, metadata_fingerprint, selected_rank FROM sample_set_member WHERE sample_set_id = $id ORDER BY selected_rank;";
            command.Parameters.AddWithValue("$id", sampleSetId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                members.Add(new SampleMember(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4)));
            }
        }

        return (sampleSet, members);
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
