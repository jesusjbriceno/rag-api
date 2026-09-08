using Rag.Companion.Adapters;
using Rag.Companion.LibreOffice;

namespace Rag.Companion.Tests;

public sealed class AdapterRegistryTests : IDisposable
{
    private readonly string _dir;

    public AdapterRegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "registry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => TryDelete(_dir);

    private static AdapterRegistry CreateTextRegistry() =>
        new(new Dictionary<string, ITextAdapter>(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = new TxtAdapter(),
            [".md"] = new MarkdownAdapter(),
        });

    [Fact]
    public async Task ExtractNormalizedTextAsync_dispatches_by_extension_case_insensitively()
    {
        var registry = CreateTextRegistry();
        var path = Path.Combine(_dir, "NOTE.TXT");
        await File.WriteAllTextAsync(path, "Hello\r\nWorld");

        var text = await registry.ExtractNormalizedTextAsync(path);

        Assert.Equal("Hello\nWorld\n", text);
    }

    [Fact]
    public async Task ExtractNormalizedTextAsync_fails_on_unknown_extension()
    {
        var registry = CreateTextRegistry();
        var path = Path.Combine(_dir, "file.xyz");
        await File.WriteAllTextAsync(path, "data");

        var ex = await Assert.ThrowsAsync<ExtractionException>(() => registry.ExtractNormalizedTextAsync(path));

        Assert.Equal(ExtractionErrorCodes.ExtractionFailed, ex.ErrorCode);
    }

    [Fact]
    public async Task ExtractNormalizedTextAsync_accepts_exactly_one_mib()
    {
        var registry = CreateTextRegistry();
        var path = Path.Combine(_dir, "at-limit.txt");
        await File.WriteAllTextAsync(path, new string('a', AdapterRegistry.MaxNormalizedUtf8Bytes - 1)); // +1 LF = 1 MiB

        var text = await registry.ExtractNormalizedTextAsync(path);

        Assert.Equal(AdapterRegistry.MaxNormalizedUtf8Bytes, System.Text.Encoding.UTF8.GetByteCount(text));
    }

    [Fact]
    public async Task ExtractNormalizedTextAsync_fails_on_one_mib_plus_one_byte()
    {
        var registry = CreateTextRegistry();
        var path = Path.Combine(_dir, "over-limit.txt");
        await File.WriteAllTextAsync(path, new string('a', AdapterRegistry.MaxNormalizedUtf8Bytes)); // +1 LF = 1 MiB + 1

        var ex = await Assert.ThrowsAsync<ExtractionException>(() => registry.ExtractNormalizedTextAsync(path));

        Assert.Equal(ExtractionErrorCodes.ExtractedTextTooLarge, ex.ErrorCode);
    }

    [Fact]
    public void SerializeIngestionPayload_uses_data_plane_field_names()
    {
        var json = AdapterRegistry.SerializeIngestionPayload(new TxtIngestionPayload
        {
            FileName = "a.txt",
            Content = "x",
            ExternalReference = "ref",
        });

        Assert.Contains("\"file_name\":\"a.txt\"", json);
        Assert.Contains("\"content\":\"x\"", json);
        Assert.Contains("\"external_reference\":\"ref\"", json);
    }

    [Fact]
    public void SerializeIngestionPayload_accepts_exactly_one_mib_and_rejects_one_more_byte()
    {
        var empty = AdapterRegistry.SerializeIngestionPayload(new TxtIngestionPayload
        {
            FileName = "a.txt",
            Content = string.Empty,
            ExternalReference = new string('0', 64),
        });
        var overhead = empty.Length;

        var atLimit = new TxtIngestionPayload
        {
            FileName = "a.txt",
            Content = new string('a', AdapterRegistry.MaxSerializedJsonBytes - overhead),
            ExternalReference = new string('0', 64),
        };
        var atLimitJson = AdapterRegistry.SerializeIngestionPayload(atLimit);
        Assert.Equal(AdapterRegistry.MaxSerializedJsonBytes, atLimitJson.Length);

        var overLimit = new TxtIngestionPayload
        {
            FileName = "a.txt",
            Content = new string('a', AdapterRegistry.MaxSerializedJsonBytes - overhead + 1),
            ExternalReference = new string('0', 64),
        };
        var ex = Assert.Throws<ExtractionException>(() => AdapterRegistry.SerializeIngestionPayload(overLimit));
        Assert.Equal(ExtractionErrorCodes.ExtractedTextTooLarge, ex.ErrorCode);
    }

    [Fact]
    public async Task CreateDefault_registers_legacy_doc_adapter()
    {
        var registry = AdapterRegistry.CreateDefault(new LibreOfficeRunner("soffice.com"));
        var path = Path.Combine(_dir, "invalid.doc");
        await File.WriteAllTextAsync(path, "not an OLE document");

        var error = await Assert.ThrowsAsync<ExtractionException>(() => registry.ExtractNormalizedTextAsync(path));

        Assert.Equal(ExtractionErrorCodes.ExtractionFailed, error.ErrorCode);
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
