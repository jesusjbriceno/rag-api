namespace Rag.Companion.Adapters;

/// <summary>
/// Machine-readable error codes surfaced on final per-file failures and companion terminal events.
/// These are the only codes the adapter layer emits; they never carry paths, content, or commands.
/// </summary>
public static class ExtractionErrorCodes
{
    public const string ExtractionFailed = "extraction_failed";
    public const string ExtractedTextTooLarge = "extracted_text_too_large";
}
