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

// ---------------------------------------------------------------------------
// Client lifecycle handlers.
// ---------------------------------------------------------------------------

public sealed class CreateClientHandler(IAdminRepository repository)
{
    public async Task<AdminCreateClientResult> HandleAsync(
        AdminActor actor,
        Guid idempotencyKey,
        string fingerprint,
        string? name,
        string? description,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
        {
            throw new ArgumentException("A client name containing at most 200 characters is required.", nameof(name));
        }

        var now = DateTimeOffset.UtcNow;
        var reservation = await repository.ReserveOperationAsync(actor.AppId, idempotencyKey, fingerprint, now, cancellationToken);
        switch (reservation.Disposition)
        {
            case AdminReservationDisposition.Replayed:
                if (Guid.TryParse(reservation.SafeResult, out var replayedId) &&
                    await repository.FindClientByIdAsync(replayedId, cancellationToken) is { } replayedClient)
                {
                    return new AdminCreateClientResult(AdminSupport.ToClientMetadata(replayedClient), true);
                }

                throw new AdminConflictException("idempotency_conflict");
            case AdminReservationDisposition.InProgress:
                throw new AdminConflictException("in_progress", retryAfterSeconds: 1);
            case AdminReservationDisposition.FingerprintMismatch:
                throw new AdminConflictException("idempotency_conflict");
        }

        var normalizedName = name.Trim();
        if (await repository.FindClientByNameAsync(normalizedName, cancellationToken) is not null)
        {
            await repository.AbandonReservedOperationAsync(cancellationToken);
            throw new AdminConflictException("duplicate");
        }

        var client = new ServiceClient(Guid.NewGuid(), normalizedName, now, description);
        repository.AddClient(client);
        repository.AddAuditEvent(
            actor,
            "create_client",
            "succeeded",
            now,
            "client",
            client.Id.ToString("D"),
            idempotencyKey.ToString("D"),
            AdminSupport.BuildClientAllowlistedJson(normalizedName, client.Description));

        try
        {
            await repository.CompleteReservedOperationAsync(client.Id.ToString("D"), cancellationToken);
        }
        catch (Exception exception) when (repository.IsClientNameViolation(exception))
        {
            await repository.AbandonReservedOperationAsync(CancellationToken.None);
            throw new AdminConflictException("duplicate");
        }

        return new AdminCreateClientResult(AdminSupport.ToClientMetadata(client), false);
    }
}

public sealed class ListClientsHandler(IAdminRepository repository)
{
    public async Task<AdminClientPage> HandleAsync(int? limit, string? cursor, CancellationToken cancellationToken = default)
    {
        var pageSize = AdminSupport.ResolveLimit(limit);
        var key = AdminSupport.ResolveCursor(cursor);
        var page = await repository.ListClientsAsync(pageSize, key, cancellationToken);
        var nextCursor = page.HasMore
            ? AdminCursor.Encode(new AdminCursorKey(page.Items[^1].CreatedAt, page.Items[^1].Id))
            : null;
        return new AdminClientPage(page.Items.Select(AdminSupport.ToClientMetadata).ToList(), nextCursor);
    }
}

public sealed class GetClientDetailHandler(IAdminRepository repository)
{
    public async Task<AdminClientDetail> HandleAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        var client = await repository.FindClientByIdAsync(clientId, cancellationToken)
            ?? throw new ResourceNotFoundException();
        var credentials = await repository.ListCredentialsByClientAsync(clientId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return new AdminClientDetail(
            AdminSupport.ToClientMetadata(client),
            credentials.Select(credential => AdminSupport.ToCredentialMetadata(credential, now)).ToList());
    }
}

internal static class AdminSupport
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public static AdminClientMetadata ToClientMetadata(ServiceClient client) =>
        new(client.Id, client.Name, client.Description, client.CreatedAt);

    public static AdminCredentialMetadata ToCredentialMetadata(ClientCredential credential, DateTimeOffset now) =>
        new(
            credential.Id,
            credential.ServiceClientId,
            credential.KeyId,
            credential.Description,
            credential.Version,
            ComputeState(credential, now),
            credential.CreatedAt,
            credential.ExpiresAt,
            credential.LastRotatedAt,
            credential.RevokedAt);

    public static int ResolveLimit(int? limit)
    {
        var size = limit ?? DefaultPageSize;
        if (size < 1 || size > MaxPageSize)
        {
            throw new ArgumentException("Page size must be between 1 and 100.", nameof(limit));
        }

        return size;
    }

    public static AdminCursorKey? ResolveCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        return AdminCursor.TryDecode(cursor)
            ?? throw new ArgumentException("The cursor is invalid.", nameof(cursor));
    }

    public static string? BuildClientAllowlistedJson(string name, string? description) =>
        description is null
            ? JsonSerializer.Serialize(new { name })
            : JsonSerializer.Serialize(new { name, description });

    private static string ComputeState(ClientCredential credential, DateTimeOffset now) =>
        credential.Status == CredentialStatus.Revoked ? "revoked"
        : credential.ExpiresAt is not null && credential.ExpiresAt <= now ? "expired"
        : "active";
