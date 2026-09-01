namespace Rag.Companion.Adapters;

/// <summary>
/// Raised when a supported file cannot be extracted to UTF-8 text, or when the extracted result
/// exceeds a hard size limit. Carries a machine-readable <see cref="ErrorCode"/> but never the
/// source path, content, or command used.
/// </summary>
public sealed class ExtractionException : Exception
{
    public ExtractionException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public ExtractionException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
