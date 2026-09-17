namespace Rag.HistoricalLoader.Core.Lifecycle;

/// <summary>
/// Machine-readable classification of one remote operation's outcome. Unit 6 keeps this bounded and
/// content-free; the real client (Unit 9) maps concrete HTTP statuses onto these classes.
/// </summary>
public enum ApiOutcome
{
    /// <summary>The server accepted the operation.</summary>
    Success,

    /// <summary>A clean transport/boundary failure; the server did not accept the mutation.</summary>
    TransientFailure,

    /// <summary>The operation was dispatched but its outcome is unknown (e.g. response lost).</summary>
    UnknownOutcome,

    /// <summary>Authentication/authorization failure (Cloudflare denial, 401, insufficient scope).</summary>
    AuthFailure,

    /// <summary>Contract/data conflict (hash mismatch, idempotency fingerprint conflict, malformed response).</summary>
    ContractFailure,
}

/// <summary>Minimal, idempotency-keyed mutation input for the fake engine.</summary>
public sealed record ApiOperation(string IdempotencyKey, string SourceDocumentKey, string? ContentSha256);

/// <summary>
/// Outcome of one remote mutation. <see cref="Pending"/> is meaningful only for poll responses where
/// the server reports the operation is still processing.
/// </summary>
public sealed record ApiOperationResult(
    ApiOutcome Outcome,
    string? RemoteId = null,
    string? ErrorCode = null,
    bool Pending = false);

/// <summary>
/// The engine's single remote-API abstraction. Mutations (reserve/upload/commit) and the status poll
/// are separate calls so the attempt-accounting rules for each stay explicit.
/// </summary>
public interface IHistoricalApiClient
{
    Task<ApiOperationResult> ReserveAsync(ApiOperation operation, CancellationToken cancellationToken = default);

    Task<ApiOperationResult> UploadAsync(ApiOperation operation, CancellationToken cancellationToken = default);

    Task<ApiOperationResult> CommitAsync(ApiOperation operation, CancellationToken cancellationToken = default);

    Task<ApiOperationResult> PollAsync(ApiOperation operation, CancellationToken cancellationToken = default);
}
