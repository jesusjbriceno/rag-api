namespace Rag.HistoricalLoader.Core.Extraction;

/// <summary>Terminal classification of one extraction attempt.</summary>
public enum ExtractionOutcome
{
    /// <summary>Normalized text produced; <see cref="ExtractionResult.NormalizedText"/> is present.</summary>
    Completed,

    /// <summary>Document-classified failure; the candidate is skipped and later candidates continue.</summary>
    SkippedDocument,

    /// <summary>Unexpected or capacity failure, classified separately from a document skip.</summary>
    Error,
}

/// <summary>
/// Machine-readable extraction error codes. Bounded and content-free; they never carry document
/// content, paths, credentials, or tokens.
/// </summary>
public static class ExtractionErrorCodes
{
    /// <summary>The legacy .doc format requires a LibreOffice process that is unavailable.</summary>
    public const string DocLibreOfficeRequired = "doc_libreoffice_required";

    /// <summary>No adapter is available for the requested format.</summary>
    public const string ExtractorUnavailable = "extractor_unavailable";

    /// <summary>The requested source path is missing or empty.</summary>
    public const string SourcePathUnavailable = "source_path_unavailable";

    /// <summary>The source changed between snapshot and extraction (drift); re-inventory is required.</summary>
    public const string SourceChanged = "source_changed";
}

/// <summary>
/// Normalized-text product of a single extraction. <see cref="NormalizedText"/> is non-null only on
/// <see cref="ExtractionOutcome.Completed"/>; otherwise <see cref="ErrorCode"/> carries the
/// classification.
/// </summary>
public sealed record ExtractionResult(
    ExtractionOutcome Outcome,
    string? NormalizedText,
    string? ErrorCode);
