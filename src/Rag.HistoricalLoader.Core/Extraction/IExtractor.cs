namespace Rag.HistoricalLoader.Core.Extraction;

/// <summary>
/// The single extraction abstraction the engine depends on. The engine composes a concrete
/// <see cref="IExtractor"/> at startup — a <see cref="FakeTextExtractor"/> for tests and proof runs,
/// or a future adapted real extractor — and never references a concrete Companion adapter or
/// namespace. Implementations are swappable at composition time.
/// </summary>
public interface IExtractor
{
    /// <summary>
    /// Extracts normalized text for the confined snapshot described by <paramref name="request"/>.
    /// Implementations return a classified <see cref="ExtractionResult"/> for document-level
    /// failures instead of throwing; only cancellation propagates.
    /// </summary>
    Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default);
}
