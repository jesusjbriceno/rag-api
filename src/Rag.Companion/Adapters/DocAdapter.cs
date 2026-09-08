using Rag.Companion.LibreOffice;

namespace Rag.Companion.Adapters;

/// <summary>
/// Extracts legacy DOC (Word 97-2003) files. The OLE Compound File magic is validated before any
/// process is started; non-OLE input is rejected outright. Valid OLE input is converted to UTF-8
/// text by the pinned LibreOffice runner, using isolated per-run profile and output directories.
/// </summary>
public sealed class DocAdapter : ITextAdapter
{
    private static readonly byte[] OleMagic = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    private readonly ILibreOfficeRunner _runner;

    public DocAdapter(ILibreOfficeRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    public async Task<string> ExtractAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOleMagic(sourcePath);

        var workingDirectory = Path.Combine(Path.GetTempPath(), "rag-companion-doc-" + Guid.NewGuid().ToString("N"));
        var profileDirectory = Path.Combine(workingDirectory, "profile");
        var outputDirectory = Path.Combine(workingDirectory, "out");

        try
        {
            return await _runner.ConvertAsync(sourcePath, profileDirectory, outputDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (LibreOfficeException ex)
        {
            throw new ExtractionException(ExtractionErrorCodes.ExtractionFailed, "DOC conversion failed.", ex);
        }
        finally
        {
            TryDelete(workingDirectory);
        }
    }

    private static void EnsureOleMagic(string sourcePath)
    {
        byte[] header;
        try
        {
            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            header = new byte[OleMagic.Length];
            var read = stream.Read(header, 0, header.Length);
            if (read < OleMagic.Length)
            {
                throw new ExtractionException(ExtractionErrorCodes.ExtractionFailed, "The file is too small to be an OLE (DOC) document.");
            }
        }
        catch (ExtractionException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new ExtractionException(ExtractionErrorCodes.ExtractionFailed, "Unable to read the source file.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ExtractionException(ExtractionErrorCodes.ExtractionFailed, "Access to the source file was denied.", ex);
        }

        if (!header.AsSpan().SequenceEqual(OleMagic))
        {
            throw new ExtractionException(ExtractionErrorCodes.ExtractionFailed, "The file is not an OLE (DOC) document.");
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
