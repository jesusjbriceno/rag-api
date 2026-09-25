using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Rag.Companion.Adapters;

namespace Rag.Companion.Tests;

public sealed class TextAdapterTests : IDisposable
{
    private readonly string _dir;

    public TextAdapterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "text-adapter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => TryDelete(_dir);

    [Fact]
    public async Task TxtAdapter_reads_utf8_text()
    {
        var path = Path.Combine(_dir, "note.txt");
        await File.WriteAllTextAsync(path, "plain text");

        var text = await new TxtAdapter().ExtractAsync(path);

        Assert.Equal("plain text", text);
    }

    [Fact]
    public async Task MarkdownAdapter_reads_utf8_text()
    {
        var path = Path.Combine(_dir, "README.md");
        await File.WriteAllTextAsync(path, "# Title\n\nBody");

        var text = await new MarkdownAdapter().ExtractAsync(path);

        Assert.Equal("# Title\n\nBody", text);
    }

    [Fact]
    public async Task TxtAdapter_rejects_invalid_utf8()
    {
        var path = Path.Combine(_dir, "broken.txt");
        await File.WriteAllBytesAsync(path, [0x48, 0x69, 0xFF, 0xFE]);

        var ex = await Assert.ThrowsAsync<ExtractionException>(() => new TxtAdapter().ExtractAsync(path));

        Assert.Equal(ExtractionErrorCodes.ExtractionFailed, ex.ErrorCode);
    }

    [Fact]
    public async Task DocxAdapter_preserves_paragraph_order()
    {
        var path = Path.Combine(_dir, "ordered.docx");
        CreateDocx(path, "First", "Second", "Third");

        var text = await new DocxAdapter().ExtractAsync(path);

        Assert.Equal("First\nSecond\nThird\n", text);
    }

    [Fact]
    public async Task DocxAdapter_fails_extraction_on_non_docx_input()
    {
        var path = Path.Combine(_dir, "not-a-docx.docx");
        await File.WriteAllTextAsync(path, "this is plain text, not an Open XML package");

        var ex = await Assert.ThrowsAsync<ExtractionException>(() => new DocxAdapter().ExtractAsync(path));

        Assert.Equal(ExtractionErrorCodes.ExtractionFailed, ex.ErrorCode);
    }

    private static void CreateDocx(string path, params string[] paragraphs)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        var body = new Body();
        foreach (var text in paragraphs)
        {
            body.AppendChild(new Paragraph(new Run(new Text(text))));
        }

        mainPart.Document = new Document(body);
        mainPart.Document.Save();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
