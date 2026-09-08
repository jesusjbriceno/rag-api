using System.Net;

namespace Rag.Companion.Ingestion;

/// <summary>
/// Raised when the data-plane ingestion API or its operation polling cannot complete a file.
/// Carries a machine-readable <see cref="ErrorCode"/>, a transient flag that the retry policy
/// honours, and optionally the HTTP status that produced it. It never carries content or credentials.
/// </summary>
public sealed class IngestionException : Exception
{
    public IngestionException(
        string errorCode,
        string message,
        bool isTransient = false,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        IsTransient = isTransient;
        StatusCode = statusCode;
    }

    public string ErrorCode { get; }

    public bool IsTransient { get; }

    public HttpStatusCode? StatusCode { get; }
}
