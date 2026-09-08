namespace Rag.Companion.Ingestion;

/// <summary>
/// Machine-readable error codes for the ingestion layer. These are surfaced on final per-file
/// failures and companion terminal events. They never carry paths, content, or credentials.
/// </summary>
public static class IngestionErrorCodes
{
    public const string TokenExchangeFailed = "token_exchange_failed";
    public const string Unauthorized = "unauthorized";
    public const string InvalidRequest = "invalid_request";
    public const string NotFound = "not_found";
    public const string PayloadTooLarge = "payload_too_large";
    public const string TemporarilyUnavailable = "temporarily_unavailable";
    public const string OperationFailed = "operation_failed";
    public const string InvalidResponse = "invalid_response";
}
