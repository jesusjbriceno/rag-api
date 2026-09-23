using Microsoft.Data.Sqlite;

namespace Rag.HistoricalLoader.Core.Benchmark;

/// <summary>A selected sample member resolved to its absolute local source path.</summary>
public sealed record ResolvedSampleMember(Guid SampleSetId, Guid CandidateId, string StratumKey, string LocalPath);

/// <summary>
/// Read-only resolution of selected sample members to absolute local paths from the loader SQLite.
/// Resolved paths are returned only to the in-memory caller (the reflection extractor) and are never
/// persisted, logged, or exported.
/// </summary>
public sealed class BenchmarkSourcePathResolver
{
    public async Task<IReadOnlyList<ResolvedSampleMember>> ResolveAsync(
        string databasePath,
        Guid sampleSetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);

        var members = new List<ResolvedSampleMember>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT m.sample_set_id, m.candidate_id, m.stratum_key, r.canonical_path, c.relative_path
            FROM sample_set_member m
            JOIN candidate c ON c.candidate_id = m.candidate_id
            JOIN source_root r ON r.root_id = c.root_id
            WHERE m.sample_set_id = $id
            ORDER BY m.selected_rank;
            """;
        command.Parameters.AddWithValue("$id", sampleSetId.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            members.Add(new ResolvedSampleMember(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                Path.GetFullPath(Path.Combine(reader.GetString(3), reader.GetString(4)))));
        }

        return members;
    }
}
