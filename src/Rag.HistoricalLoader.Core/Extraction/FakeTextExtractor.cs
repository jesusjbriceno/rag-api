using System.Security.Cryptography;
using System.Text;

namespace Rag.HistoricalLoader.Core.Extraction;

/// <summary>
/// Deterministic, in-memory <see cref="IExtractor"/> used by Units 6 and 10 and by contract tests.
/// It never opens the source file or any external process, so it is safe in CI and proof runs.
/// Synthetic normalized text is derived from the source identity (a hash of the path), never from the
/// real file bytes, and never embeds the path. Supported formats mirror the documented Companion
/// adapter surface: pdf/docx/md/txt complete; legacy .doc is skipped with
/// <see cref="ExtractionErrorCodes.DocLibreOfficeRequired"/>; any other format is skipped with
/// <see cref="ExtractionErrorCodes.ExtractorUnavailable"/>.
/// </summary>
public sealed class FakeTextExtractor : IExtractor
{
    private static readonly HashSet<string> SupportedFormats = new(StringComparer.Ordinal)
    {
        "pdf",
        "docx",
        "md",
        "txt",
    };

    public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.SourcePath))
        {
            return Task.FromResult(new ExtractionResult(
                ExtractionOutcome.Error,
                null,
                ExtractionErrorCodes.SourcePathUnavailable));
        }

        var format = NormalizeFormat(request.DocumentFormat);
        if (format is null)
        {
            return Skip(ExtractionErrorCodes.ExtractorUnavailable);
        }

        if (format == "doc")
        {
            return Skip(ExtractionErrorCodes.DocLibreOfficeRequired);
        }

        if (!SupportedFormats.Contains(format))
        {
            return Skip(ExtractionErrorCodes.ExtractorUnavailable);
        }

        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.SourcePath)));
        var normalized = $"fake-normalized-text:{format}:{identity}";
        return Task.FromResult(new ExtractionResult(ExtractionOutcome.Completed, normalized, null));
    }

    private static Task<ExtractionResult> Skip(string errorCode)
        => Task.FromResult(new ExtractionResult(ExtractionOutcome.SkippedDocument, null, errorCode));

    private static string? NormalizeFormat(string? documentFormat)
    {
        var normalized = documentFormat?.Trim().TrimStart('.').ToLowerInvariant();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
