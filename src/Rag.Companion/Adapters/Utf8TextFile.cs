using System.Text;

namespace Rag.Companion.Adapters;

/// <summary>Reads a file as strict UTF-8: a leading BOM is detected and stripped, invalid bytes fail.</summary>
internal static class Utf8TextFile
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: true,
        throwOnInvalidBytes: true);

    public static string ReadAllText(string sourcePath)
    {
        try
        {
            return File.ReadAllText(sourcePath, StrictUtf8);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ExtractionException(ExtractionErrorCodes.ExtractionFailed, "The file is not valid UTF-8 text.", ex);
        }
        catch (IOException ex)
        {
            throw new ExtractionException(ExtractionErrorCodes.ExtractionFailed, "Unable to read the source file.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ExtractionException(ExtractionErrorCodes.ExtractionFailed, "Access to the source file was denied.", ex);
        }
    }
}
