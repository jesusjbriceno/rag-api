using Rag.Companion.Adapters;
using Rag.Companion.LibreOffice;

namespace Rag.Companion.Tests;

public sealed class DocAdapterTests : IDisposable
{
    private static readonly byte[] OleMagic = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    private readonly string _dir;

    public DocAdapterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "doc-adapter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => TryDelete(_dir);

    [Fact]
    public async Task DocAdapter_rejects_non_ole_input_before_running_any_process()
    {
        var path = Path.Combine(_dir, "not-a-doc.doc");
        await File.WriteAllTextAsync(path, "plain text masquerading as a DOC");
        var runner = new StubRunner((_, _, _) => "should not be reached");

        var ex = await Assert.ThrowsAsync<ExtractionException>(() => new DocAdapter(runner).ExtractAsync(path));

        Assert.Equal(ExtractionErrorCodes.ExtractionFailed, ex.ErrorCode);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task DocAdapter_converts_ole_input_via_runner()
    {
        var path = Path.Combine(_dir, "real.doc");
        await File.WriteAllBytesAsync(path, [.. OleMagic, 0x00, 0x01, 0x02]);
        var runner = new StubRunner((_, profile, output) =>
        {
            Assert.EndsWith("profile", profile);
            Assert.EndsWith("out", output);
            return "converted doc text";
        });

        var text = await new DocAdapter(runner).ExtractAsync(path);

        Assert.Equal("converted doc text", text);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task DocAdapter_maps_runner_failure_to_extraction_failed()
    {
        var path = Path.Combine(_dir, "real.doc");
        await File.WriteAllBytesAsync(path, [.. OleMagic, 0x00]);
        var runner = new StubRunner((_, _, _) => throw new LibreOfficeException("converter exploded"));

        var ex = await Assert.ThrowsAsync<ExtractionException>(() => new DocAdapter(runner).ExtractAsync(path));

        Assert.Equal(ExtractionErrorCodes.ExtractionFailed, ex.ErrorCode);
    }

    private sealed class StubRunner : ILibreOfficeRunner
    {
        private readonly Func<string, string, string, string> _onConvert;

        public StubRunner(Func<string, string, string, string> onConvert) => _onConvert = onConvert;

        public int Calls { get; private set; }

        public Task<string> ConvertAsync(string sourceDocPath, string profileDirectory, string outputDirectory, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_onConvert(sourceDocPath, profileDirectory, outputDirectory));
        }
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
