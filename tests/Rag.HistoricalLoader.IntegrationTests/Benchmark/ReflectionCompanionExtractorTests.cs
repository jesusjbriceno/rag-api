using System.IO.Compression;
using System.Text;
using Rag.HistoricalLoader.Core.Benchmark;
using Rag.HistoricalLoader.Engine;

namespace Rag.HistoricalLoader.IntegrationTests.Benchmark;

public sealed class ReflectionCompanionExtractorTests : IDisposable
{
    // Deterministic two-page unencrypted PDF ("Page One" / "Page Two"); reused verbatim from Companion.Tests.
    private const string TwoPagePdf =
        "JVBERi0xLjQKMSAwIG9iago8PCAvVHlwZSAvQ2F0YWxvZyAvUGFnZXMgMiAwIFIgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL1R5cGUgL1BhZ2VzIC9LaWRzIFszIDAgUiA2IDAgUl0gL0NvdW50IDIgPj4KZW5kb2JqCjMgMCBvYmoKPDwgL1R5cGUgL1BhZ2UgL1BhcmVudCAyIDAgUiAvTWVkaWFCb3ggWzAgMCA2MTIgNzkyXSAvQ29udGVudHMgNCAwIFIgL1Jlc291cmNlcyA8PCAvRm9udCA8PCAvRjEgNSAwIFIgPj4gPj4gPj4KZW5kb2JqCjQgMCBvYmoKPDwgL0xlbmd0aCAzOSA+PgpzdHJlYW0KQlQgL0YxIDI0IFRmIDEwMCA3MDAgVGQgKFBhZ2UgT25lKSBUaiBFVAplbmRzdHJlYW0KZW5kb2JqCjUgMCBvYmoKPDwgL1R5cGUgL0ZvbnQgL1N1YnR5cGUgL1R5cGUxIC9CYXNlRm9udCAvSGVsdmV0aWNhID4+CmVuZG9iago2IDAgb2JqCjw8IC9UeXBlIC9QYWdlIC9QYXJlbnQgMiAwIFIgL01lZGlhQm94IFswIDAgNjEyIDc5Ml0gL0NvbnRlbnRzIDcgMCBSIC9SZXNvdXJjZXMgPDwgL0ZvbnQgPDwgL0YxIDUgMCBSID4+ID4+ID4+CmVuZG9iago3IDAgb2JqCjw8IC9MZW5ndGggMzkgPj4Kc3RyZWFtCkJUIC9GMSAyNCBUZiAxMDAgNzAwIFRkIChQYWdlIFR3bykgVGogRVQKZW5kc3RyZWFtCmVuZG9iagp4cmVmCjAgOAowMDAwMDAwMDAwIDY1NTM1IGYgCnRyYWlsZXIKPDwgL1NpemUgOCAvUm9vdCAxIDAgUiA+PgpzdGFydHhyZWYKMAolJUVPRg==";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"reflect-{Guid.NewGuid():N}");

    public ReflectionCompanionExtractorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task Extract_Markdown_ReturnsByteCountOnly()
    {
        var path = Path.Combine(_dir, "README.md");
        var content = "# Title\n\nBody";
        await File.WriteAllTextAsync(path, content);

        var result = await CreateExtractor().ExtractAsync(path);

        Assert.Equal("completed", result.Outcome);
        Assert.Null(result.ErrorCode);
        Assert.Equal(Encoding.UTF8.GetByteCount(content), result.NormalizedTextBytes);
    }

    [Fact]
    public async Task Extract_Docx_ReturnsByteCountOnly()
    {
        var path = Path.Combine(_dir, "note.docx");
        WriteMinimalDocx(path, "Hello DOCX");

        var result = await CreateExtractor().ExtractAsync(path);

        Assert.Equal("completed", result.Outcome);
        Assert.Null(result.ErrorCode);
        Assert.Equal(Encoding.UTF8.GetByteCount("Hello DOCX\n"), result.NormalizedTextBytes);
    }

    [Fact]
    public async Task Extract_Pdf_ReturnsByteCountOnly()
    {
        var path = Path.Combine(_dir, "two.pdf");
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(TwoPagePdf));

        var result = await CreateExtractor().ExtractAsync(path);

        Assert.Equal("completed", result.Outcome);
        Assert.Null(result.ErrorCode);
        Assert.Equal(Encoding.UTF8.GetByteCount("Page One\nPage Two\n"), result.NormalizedTextBytes);
    }

    [Fact]
    public async Task Extract_Doc_ReportsStableLibreOfficeRequired()
    {
        var path = Path.Combine(_dir, "legacy.doc");
        await File.WriteAllBytesAsync(path, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00]);

        var result = await CreateExtractor().ExtractAsync(path);

        Assert.Equal("error", result.Outcome);
        Assert.Equal(LocalExtractionErrorCodes.DocLibreOfficeRequired, result.ErrorCode);
        Assert.Equal(0, result.NormalizedTextBytes);
    }

    [Fact]
    public async Task Extract_PdfInvalidContent_ReportsStableUnavailable()
    {
        // Proves the PdfAdapter was genuinely loaded and invoked: its parse failure is mapped
        // to a stable code rather than a fake success or a crash.
        var path = Path.Combine(_dir, "broken.pdf");
        await File.WriteAllTextAsync(path, "not a real pdf");

        var result = await CreateExtractor().ExtractAsync(path);

        Assert.Equal("error", result.Outcome);
        Assert.Equal(LocalExtractionErrorCodes.ExtractorUnavailable, result.ErrorCode);
        Assert.Equal(0, result.NormalizedTextBytes);
    }

    [Fact]
    public async Task Extract_UnsupportedExtensionOrMissingAssembly_ReportsStableUnavailable()
    {
        var missing = new ReflectionCompanionExtractor(Path.Combine(_dir, "missing", "Rag.Companion.dll"));
        Assert.Equal(LocalExtractionErrorCodes.ExtractorUnavailable, (await missing.ExtractAsync("x.md")).ErrorCode);

        var extractor = CreateExtractor();
        Assert.Equal(LocalExtractionErrorCodes.ExtractorUnavailable, (await extractor.ExtractAsync(Path.Combine(_dir, "data.xyz"))).ErrorCode);
    }

    private static ReflectionCompanionExtractor CreateExtractor()
    {
        var path = LocateCompanionAssembly();
        Assert.True(File.Exists(path), $"Rag.Companion assembly not found at '{path}'. Build Rag.sln first.");
        return new ReflectionCompanionExtractor(path);
    }

    private static string LocateCompanionAssembly()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rag.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new InvalidOperationException("Repository root (Rag.sln) not found above the test output directory.");
        foreach (var config in new[] { "Release", "Debug" })
        {
            var candidate = Path.Combine(root, "src", "Rag.Companion", "bin", config, "net10.0", "win-x64", "Rag.Companion.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(root, "src", "Rag.Companion", "bin", "Release", "net10.0", "win-x64", "Rag.Companion.dll");
    }

    private static void WriteMinimalDocx(string path, string text)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        AddEntry(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        AddEntry(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        AddEntry(zip, "word/document.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>" + text + "</w:t></w:r></w:p></w:body></w:document>");
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
