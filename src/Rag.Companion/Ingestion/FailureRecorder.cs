using System.Text.Json;

namespace Rag.Companion.Ingestion;

/// <summary>
/// Records one final per-file failure as a single JSON line containing only a machine-readable
/// error code. Entries intentionally omit the source path, file content, and any credential, so
/// the local structured log can be inspected without leaking secrets or source identity.
/// </summary>
public sealed class FailureRecorder
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly TextWriter _writer;

    public FailureRecorder(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    /// <summary>Appends one JSON line <c>{"errorCode":"..."}</c> for a final per-file failure.</summary>
    public Task RecordAsync(string errorCode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        var entry = JsonSerializer.Serialize(new FailureEntry(errorCode), Options);
        return _writer.WriteLineAsync(entry.AsMemory(), cancellationToken);
    }

    private sealed record FailureEntry(string ErrorCode);
}
