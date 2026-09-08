namespace Rag.Companion.LibreOffice;

/// <summary>Converts a legacy DOC file to UTF-8 text using a pinned LibreOffice installation.</summary>
public interface ILibreOfficeRunner
{
    Task<string> ConvertAsync(
        string sourceDocPath,
        string profileDirectory,
        string outputDirectory,
        CancellationToken cancellationToken = default);
}
