using System.Globalization;
using System.Text;
using System.Text.Json;
using Rag.Domain;

namespace Rag.Application;

// ---------------------------------------------------------------------------
// Metadata representations (secret-free; safe for API responses and audit).
// ---------------------------------------------------------------------------

public sealed record AdminClientMetadata(Guid Id, string Name, string? Description, DateTimeOffset CreatedAt);

public sealed record AdminCredentialMetadata(
    Guid Id,
    Guid ClientId,
    string KeyId,
    string? Description,
    int Version,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastRotatedAt,
    DateTimeOffset? RevokedAt);

public sealed record AdminClientDetail(AdminClientMetadata Client, IReadOnlyList<AdminCredentialMetadata> Credentials);

public sealed record AdminClientPage(IReadOnlyList<AdminClientMetadata> Items, string? NextCursor);

public sealed record AdminAuditEventMetadata(
    Guid Id,
    string ActorSubject,
    string AppId,
    string Action,
    string Outcome,
    DateTimeOffset OccurredAt,
    string? TargetType,
    string? TargetId,
    string? Operation,
    string? AllowlistedJson);

public sealed record AdminAuditPage(IReadOnlyList<AdminAuditEventMetadata> Items, string? NextCursor);

public sealed record AdminCredentialDelivery(AdminCredentialMetadata Credential, string Secret);

public sealed record AdminCreateClientResult(AdminClientMetadata Client, bool Replayed);

// ---------------------------------------------------------------------------
// Conflict signalling.
// ---------------------------------------------------------------------------

public sealed class AdminConflictException(string code, int? retryAfterSeconds = null) : Exception
{
    public string Code { get; } = code;

    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

// ---------------------------------------------------------------------------
// Idempotency reservation.
// ---------------------------------------------------------------------------

public enum AdminReservationDisposition
{
    Proceed,
    Replayed,
    InProgress,
    FingerprintMismatch,
}

public sealed record AdminOperationReservation(AdminReservationDisposition Disposition, string? SafeResult);

// ---------------------------------------------------------------------------
// Keyset cursor pagination.
// ---------------------------------------------------------------------------

public sealed record AdminCursorKey(DateTimeOffset Timestamp, Guid Id);

public static class AdminCursor
{
    private const int CurrentVersion = 1;

    public static string Encode(AdminCursorKey key) => Encode(key.Timestamp, key.Id);

    public static string Encode(DateTimeOffset timestamp, Guid id)
    {
        var payload = string.Join(
            '|',
            CurrentVersion.ToString(CultureInfo.InvariantCulture),
            timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            id.ToString("D"));
        return Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
    }

    public static AdminCursorKey? TryDecode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var payload = Encoding.UTF8.GetString(Base64UrlDecode(cursor));
            var parts = payload.Split('|');
            if (parts.Length != 3 ||
                parts[0] != CurrentVersion.ToString(CultureInfo.InvariantCulture) ||
                !DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp) ||
                !Guid.TryParseExact(parts[2], "D", out var id))
            {
                return null;
            }

            return new AdminCursorKey(timestamp, id);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Convert.FromBase64String(normalized);
    }
}

// ---------------------------------------------------------------------------
// Persistence contract.
// ---------------------------------------------------------------------------

public sealed record AdminPage<T>(IReadOnlyList<T> Items, bool HasMore);

public interface IAdminRepository
{
    Task<AdminOperationReservation> ReserveOperationAsync(
        string appId,
        Guid idempotencyKey,
        string fingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task CompleteReservedOperationAsync(string safeResult, CancellationToken cancellationToken);

    Task AbandonReservedOperationAsync(CancellationToken cancellationToken);

    Task<ServiceClient?> FindClientByIdAsync(Guid clientId, CancellationToken cancellationToken);

    Task<ServiceClient?> FindClientByNameAsync(string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<ClientCredential>> ListCredentialsByClientAsync(Guid clientId, CancellationToken cancellationToken);

    Task<ClientCredential?> FindCredentialByIdAsync(Guid credentialId, CancellationToken cancellationToken);

    Task<AdminPage<ServiceClient>> ListClientsAsync(int limit, AdminCursorKey? cursor, CancellationToken cancellationToken);

    Task<AdminPage<AdminAuditEventMetadata>> ListAuditAsync(int limit, AdminCursorKey? cursor, CancellationToken cancellationToken);

    void AddClient(ServiceClient client);

    void AddCredential(ClientCredential credential);

    void AddAuditEvent(
        AdminActor actor,
        string action,
        string outcome,
        DateTimeOffset occurredAt,
        string? targetType,
        string? targetId,
        string? operation,
        string? allowlistedJson);

    bool IsClientNameViolation(Exception exception);

    bool IsConcurrencyViolation(Exception exception);
}
