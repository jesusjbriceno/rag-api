using System.Text;
using UglyToad.PdfPig;

namespace Rag.Companion.Adapters;

/// <summary>
/// Extracts PDF text via PdfPig, returning pages in document order (one page per section).
/// Encrypted (password-protected) documents fail with <c>extraction_failed</c>.
/// </summary>
public sealed class PdfAdapter : ITextAdapter
{
    public Task<string> ExtractAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var document = PdfDocument.Open(sourcePath);
            var builder = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                builder.Append(page.Text);
                builder.Append('\n');
            }

            return Task.FromResult(builder.ToString());
        }
        catch (ExtractionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ExtractionException(ExtractionErrorCodes.ExtractionFailed, "Failed to extract PDF text.", ex);
        }
    }
}
