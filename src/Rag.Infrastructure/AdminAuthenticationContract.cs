namespace Rag.Infrastructure;

/// <summary>
/// Shared downstream administrator-authentication wire contract consumed by both the
/// AdminApp BFF and the Rag.Api authentication handler. Centralising these values
/// prevents header-name and authenticated-body-limit drift between the two processes.
/// </summary>
public static class AdminAuthenticationContract
{
    public const string AppIdHeader = "X-Admin-App-Id";
    public const string KeyIdHeader = "X-Admin-Key-Id";
    public const string TimestampHeader = "X-Admin-Timestamp";
    public const string SignatureHeader = "X-Admin-Signature";
    public const string AssertionHeader = "X-Admin-Assertion";
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public const int MaxBodyBytes = 1_048_576;
}
