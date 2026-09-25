using Microsoft.Data.Sqlite;

namespace Rag.HistoricalLoader.Engine.Control;

/// <summary>One selected member of an approved batch, resolved to the local source identity the pipeline reads.</summary>
public sealed record SampleBatchDocument(Guid CandidateId, string SourcePath);

/// <summary>
/// An approved sample batch as the engine resolves it from durable rows: the gate decision plus the selected
/// members. The wire never carries these facts — the client sends only the opaque batch id.
/// </summary>
public sealed record ResolvedSampleBatch(
    Guid SampleSetId,
    Guid ManifestId,
    bool GatePassed,
    IReadOnlyList<SampleBatchDocument> Documents);

/// <summary>
/// Read-only resolution of an operator-approved batch (a frozen sample set) to the selected candidates and
/// their local source paths, joined from the same loader SQLite the inventory and sampling steps wrote. The
/// resolved paths stay in engine memory and are never projected, so a resolved path cannot reach the wire.
/// </summary>
public sealed class ControlSampleBatchResolver : IAsyncDisposable
{
    private const string SelectSetSql = """
        SELECT manifest_id, gate_passed FROM sample_set WHERE sample_set_id = $id;
        """;

    private const string SelectMembersSql = """
        SELECT m.candidate_id, r.canonical_path, c.relative_path
        FROM sample_set_member m
        JOIN candidate c ON c.candidate_id = m.candidate_id
        JOIN source_root r ON r.root_id = c.root_id
        WHERE m.sample_set_id = $id
        ORDER BY m.selected_rank;
        """;

    private readonly string _databasePath;
    private SqliteConnection? _connection;

    public ControlSampleBatchResolver(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    /// <summary>
    /// Resolves one batch, or returns <see langword="null"/> when no such sample set is durable (an unknown
    /// batch is an explicit absence, never an empty approved batch). A store fault propagates to the caller,
    /// which maps it to a stable rejection.
    /// </summary>
    public async Task<ResolvedSampleBatch?> ResolveAsync(Guid sampleSetId, CancellationToken cancellationToken = default)
    {
        var connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);

        Guid manifestId;
        bool gatePassed;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = SelectSetSql;
            command.Parameters.AddWithValue("$id", sampleSetId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            manifestId = Guid.Parse(reader.GetString(0));
            gatePassed = reader.GetInt64(1) == 1L;
        }

        var documents = new List<SampleBatchDocument>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = SelectMembersSql;
            command.Parameters.AddWithValue("$id", sampleSetId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                documents.Add(new SampleBatchDocument(
                    Guid.Parse(reader.GetString(0)),
                    Path.GetFullPath(Path.Combine(reader.GetString(1), reader.GetString(2)))));
            }
        }

        return new ResolvedSampleBatch(sampleSetId, manifestId, gatePassed, documents);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }

    /// <summary>
    /// Opens the read-only resolution connection once per engine process. The connection is read-write at the
    /// file level because a WAL database cannot be opened read-only while its shared-memory index is absent;
    /// every statement this resolver issues is a plain read, and it never mutates a row.
    /// </summary>
    private async Task<SqliteConnection> EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        _connection = connection;
        return connection;
    }
}
