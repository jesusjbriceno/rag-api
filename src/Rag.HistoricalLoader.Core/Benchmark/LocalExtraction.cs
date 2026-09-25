namespace Rag.HistoricalLoader.Core.Benchmark;

/// <summary>Machine-readable error codes for real local extraction. Never carry paths or content.</summary>
public static class LocalExtractionErrorCodes
{
    public const string UnavailableLibreOfficeAbsent = "unavailable_libreoffice_absent";
    public const string DocLibreOfficeRequired = "doc_libreoffice_required";
    public const string ExtractorUnavailable = "extractor_unavailable";
    public const string SourcePathUnavailable = "source_path_unavailable";
}

public sealed record LocalExtractionResult(long NormalizedTextBytes, string Outcome, string? ErrorCode);

/// <summary>
/// Benchmark-only abstraction over a local text extractor. Implementations may sit behind a
/// reflection or process boundary; Core never references <c>Rag.Companion</c>.
/// </summary>
public interface ILocalTextExtractor
{
    Task<LocalExtractionResult> ExtractAsync(string sourcePath, CancellationToken cancellationToken = default);
}

/// <summary>Classifies formats by whether they require a LibreOffice process to extract.</summary>
public static class LocalExtractionPolicy
{
    public static bool RequiresLibreOffice(string extension)
        => string.Equals(extension, ".doc", StringComparison.OrdinalIgnoreCase);
}
