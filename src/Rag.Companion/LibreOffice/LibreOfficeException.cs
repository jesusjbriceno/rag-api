namespace Rag.Companion.LibreOffice;

/// <summary>Raised when the LibreOffice conversion process fails or produces invalid output.</summary>
public sealed class LibreOfficeException : Exception
{
    public LibreOfficeException(string message)
        : base(message)
    {
    }

    public LibreOfficeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
