namespace Rag.HistoricalLoader.Core.Extraction;

/// <summary>
/// Confined, content-safe input for a single extraction. <see cref="SourcePath"/> identifies the
/// snapshot the engine already confined; <see cref="DocumentFormat"/> is the normalized format token
/// used for adapter dispatch (e.g. "pdf", "docx", "md", "txt").
/// </summary>
public sealed record ExtractionRequest(string SourcePath, string DocumentFormat);
