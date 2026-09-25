using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Rag.Companion.Adapters;

/// <summary>
/// Extracts DOCX text via the Open XML SDK, returning top-level body paragraphs in document order
/// (one paragraph per line). Paragraphs nested inside tables are out of scope for this slice.
/// </summary>
public sealed class DocxAdapter : ITextAdapter
{
    public Task<string> ExtractAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var document = WordprocessingDocument.Open(sourcePath, false);
            var body = document.MainDocumentPart?.Document?.Body;
            if (body is null)
            {
                throw new ExtractionException(ExtractionErrorCodes.ExtractionFailed, "The DOCX package has no main document body.");
            }

            var builder = new StringBuilder();
            foreach (var paragraph in body.Elements<Paragraph>())
            {
                builder.Append(paragraph.InnerText);
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
            throw new ExtractionException(ExtractionErrorCodes.ExtractionFailed, "Failed to extract DOCX text.", ex);
        }
    }
}
