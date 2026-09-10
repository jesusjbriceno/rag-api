using Microsoft.Data.Sqlite;
using Rag.HistoricalLoader.Core.Data;

namespace Rag.HistoricalLoader.Core.Inventory;

public sealed record ManifestSnapshot(
    Guid ManifestId,
    int ScanVersion,
    ManifestState State,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    IReadOnlyList<Candidate> Candidates,
    int ErrorCount)
{
    public bool IsComplete => State == ManifestState.Complete;
}

/// <summary>
/// Read-only snapshot of the persisted manifest rows used to drive the atomic export.
/// It opens its own read-only connection after the single-writer store is closed.
/// </summary>
public sealed class ManifestSnapshotReader
{
    public async Task<ManifestSnapshot> LoadAsync(string databasePath, Guid manifestId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);

        int scanVersion;
        ManifestState state;
        DateTimeOffset startTime;
        DateTimeOffset? endTime;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT scan_version, state, start_time, end_time FROM manifest WHERE manifest_id = $id;";
            command.Parameters.AddWithValue("$id", manifestId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException($"Manifest '{manifestId}' does not exist.");
            }

            scanVersion = reader.GetInt32(0);
            state = (ManifestState)reader.GetInt64(1);
            startTime = new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero);
            endTime = reader.IsDBNull(3) ? null : new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero);
        }

        var candidates = new List<Candidate>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT candidate_id, manifest_id, root_id, relative_path, extension, byte_size, last_write_time, eligibility_code, discovery_error_code, metadata_fingerprint FROM candidate WHERE manifest_id = $id ORDER BY root_id, relative_path;";
            command.Parameters.AddWithValue("$id", manifestId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(new Candidate(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    Guid.Parse(reader.GetString(2)),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero),
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9)));
            }
        }

        int errorCount;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM enumeration_error WHERE manifest_id = $id;";
            command.Parameters.AddWithValue("$id", manifestId.ToString());
            errorCount = (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        return new ManifestSnapshot(manifestId, scanVersion, state, startTime, endTime, candidates, errorCount);
    }
}
