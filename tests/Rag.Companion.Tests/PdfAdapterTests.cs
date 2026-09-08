using Rag.Companion.Adapters;

namespace Rag.Companion.Tests;

public sealed class PdfAdapterTests : IDisposable
{
    // Two-page PDF, unencrypted, with "Page One" / "Page Two" text (deterministic fixture).
    private const string TwoPagePdf = "JVBERi0xLjQKMSAwIG9iago8PCAvVHlwZSAvQ2F0YWxvZyAvUGFnZXMgMiAwIFIgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL1R5cGUgL1BhZ2VzIC9LaWRzIFszIDAgUiA2IDAgUl0gL0NvdW50IDIgPj4KZW5kb2JqCjMgMCBvYmoKPDwgL1R5cGUgL1BhZ2UgL1BhcmVudCAyIDAgUiAvTWVkaWFCb3ggWzAgMCA2MTIgNzkyXSAvQ29udGVudHMgNCAwIFIgL1Jlc291cmNlcyA8PCAvRm9udCA8PCAvRjEgNSAwIFIgPj4gPj4gPj4KZW5kb2JqCjQgMCBvYmoKPDwgL0xlbmd0aCAzOSA+PgpzdHJlYW0KQlQgL0YxIDI0IFRmIDEwMCA3MDAgVGQgKFBhZ2UgT25lKSBUaiBFVAplbmRzdHJlYW0KZW5kb2JqCjUgMCBvYmoKPDwgL1R5cGUgL0ZvbnQgL1N1YnR5cGUgL1R5cGUxIC9CYXNlRm9udCAvSGVsdmV0aWNhID4+CmVuZG9iago2IDAgb2JqCjw8IC9UeXBlIC9QYWdlIC9QYXJlbnQgMiAwIFIgL01lZGlhQm94IFswIDAgNjEyIDc5Ml0gL0NvbnRlbnRzIDcgMCBSIC9SZXNvdXJjZXMgPDwgL0ZvbnQgPDwgL0YxIDUgMCBSID4+ID4+ID4+CmVuZG9iago3IDAgb2JqCjw8IC9MZW5ndGggMzkgPj4Kc3RyZWFtCkJUIC9GMSAyNCBUZiAxMDAgNzAwIFRkIChQYWdlIFR3bykgVGogRVQKZW5kc3RyZWFtCmVuZG9iagp4cmVmCjAgOAowMDAwMDAwMDAwIDY1NTM1IGYgCnRyYWlsZXIKPDwgL1NpemUgOCAvUm9vdCAxIDAgUiA+PgpzdGFydHhyZWYKMAolJUVPRg==";

    // One-page PDF encrypted with user password "secret" (R2/40-bit RC4). No password => PdfDocumentEncryptedException.
    private const string EncryptedPdf = "JVBERi0xLjQKMSAwIG9iago8PCAvVHlwZSAvQ2F0YWxvZyAvUGFnZXMgMiAwIFIgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL1R5cGUgL1BhZ2VzIC9LaWRzIFszIDAgUl0gL0NvdW50IDEgPj4KZW5kb2JqCjMgMCBvYmoKPDwgL1R5cGUgL1BhZ2UgL1BhcmVudCAyIDAgUiAvTWVkaWFCb3ggWzAgMCA2MTIgNzkyXSAvQ29udGVudHMgNCAwIFIgL1Jlc291cmNlcyA8PCAvRm9udCA8PCAvRjEgNSAwIFIgPj4gPj4gPj4KZW5kb2JqCjQgMCBvYmoKPDwgL0xlbmd0aCA0NCA+PgpzdHJlYW0KJzxk2Lji7vKNhjEF0d494zUl7G8fz3epRoXzVOrteaOQ0C1L4cOVf0BYAxgKZW5kc3RyZWFtCmVuZG9iago1IDAgb2JqCjw8IC9UeXBlIC9Gb250IC9TdWJ0eXBlIC9UeXBlMSAvQmFzZUZvbnQgL0hlbHZldGljYSA+PgplbmRvYmoKNiAwIG9iago8PCAvRmlsdGVyIC9TdGFuZGFyZCAvViAxIC9SIDIgL08gPDkyZmUwZjQ0NTRhZDRjOTY0NDY5M2YzM2MwN2NiNTRmNTg3ZGNlMWUyNjgyZmU5ZWNlYTYxMDdhMWVmNjMwZGQ+IC9VIDxhMGVkOGZlODIwZGNkYTk3NzRlYjk0M2U3Yjk2NTRmNDNkNmY1OTlmZTAwN2U0YjU0ZWI4OTRhZjIyNjVjZmE4PiAvUCAtNCA+PgplbmRvYmoKeHJlZgowIDcKMDAwMDAwMDAwMCA2NTUzNSBmIAp0cmFpbGVyCjw8IC9TaXplIDcgL1Jvb3QgMSAwIFIgL0VuY3J5cHQgNiAwIFIgL0lEIFs8NDE0MjQzMzEzMjMzMzQzNTM2MzczODM5MzAzMTMyMzMzNDM1MzYzNzM4MzkzMDMxMzIzMzM0MzUzNjM3MzgzOTMwMzE+IDw0MTQyNDMzMTMyMzMzNDM1MzYzNzM4MzkzMDMxMzIzMzM0MzUzNjM3MzgzOTMwMzEzMjMzMzQzNTM2MzczODM5MzAzMT5dID4+CnN0YXJ0eHJlZgowCiUlRU9GCg==";

    private readonly string _dir;

    public PdfAdapterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pdf-adapter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => TryDelete(_dir);

    [Fact]
    public async Task PdfAdapter_extracts_pages_in_document_order()
    {
        var path = WritePdf("two.pdf", TwoPagePdf);

        var text = await new PdfAdapter().ExtractAsync(path);

        Assert.Equal("Page One\nPage Two\n", text);
    }

    [Fact]
    public async Task PdfAdapter_fails_extraction_on_password_protected_pdf()
    {
        var path = WritePdf("encrypted.pdf", EncryptedPdf);

        var ex = await Assert.ThrowsAsync<ExtractionException>(() => new PdfAdapter().ExtractAsync(path));

        Assert.Equal(ExtractionErrorCodes.ExtractionFailed, ex.ErrorCode);
    }

    private string WritePdf(string name, string base64)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Convert.FromBase64String(base64));
        return path;
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
