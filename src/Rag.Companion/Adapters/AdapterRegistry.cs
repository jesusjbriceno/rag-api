using System.Text;
using System.Text.Json;

namespace Rag.Companion.Adapters;

/// <summary>
/// Dispatches extraction to the adapter registered for a file extension, normalizes the result,
/// and enforces the hard 1 MiB limits on normalized UTF-8 text and serialized ingestion JSON.
/// Over-limit results fail with <c>extracted_text_too_large</c> without truncation, splitting, or retry.
/// </summary>
public sealed class AdapterRegistry
{
    public const int MaxNormalizedUtf8Bytes = 1024 * 1024;
    public const int MaxSerializedJsonBytes = 1_048_576;

    private static readonly JsonSerializerOptions IngestionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IReadOnlyDictionary<string, ITextAdapter> _adapters;

    public AdapterRegistry(IReadOnlyDictionary<string, ITextAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = new Dictionary<string, ITextAdapter>(adapters, StringComparer.OrdinalIgnoreCase);
    }

    public static AdapterRegistry CreateDefault()
    {
        return new AdapterRegistry(new Dictionary<string, ITextAdapter>(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = new TxtAdapter(),
            [".md"] = new MarkdownAdapter(),
            [".docx"] = new DocxAdapter(),
            [".pdf"] = new PdfAdapter(),
        });
    }

    /// <summary>Dispatches by extension, extracts, normalizes, and returns the 1 MiB-capped UTF-8 text.</summary>
    public async Task<string> ExtractNormalizedTextAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrEmpty(extension) || !_adapters.TryGetValue(extension, out var adapter))
        {
            throw new ExtractionException(ExtractionErrorCodes.ExtractionFailed, $"No adapter is registered for '{extension}'.");
        }

        var raw = await adapter.ExtractAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var normalized = Normalizer.Normalize(raw);

        var byteCount = Encoding.UTF8.GetByteCount(normalized);
        if (byteCount > MaxNormalizedUtf8Bytes)
        {
            throw new ExtractionException(
                ExtractionErrorCodes.ExtractedTextTooLarge,
                $"Normalized text is {byteCount} bytes; the limit is {MaxNormalizedUtf8Bytes}.");
        }

        return normalized;
    }

    /// <summary>Serializes an ingestion payload and enforces the 1 MiB serialized-JSON limit.</summary>
    public static string SerializeIngestionPayload(TxtIngestionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var json = JsonSerializer.Serialize(payload, IngestionJsonOptions);
        var byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > MaxSerializedJsonBytes)
        {
            throw new ExtractionException(
                ExtractionErrorCodes.ExtractedTextTooLarge,
                $"Serialized ingestion JSON is {byteCount} bytes; the limit is {MaxSerializedJsonBytes}.");
        }

        return json;
    }
}
