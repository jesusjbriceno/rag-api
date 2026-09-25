using Microsoft.EntityFrameworkCore;
using Npgsql;
using Rag.Application;
using Rag.Domain;

namespace Rag.Infrastructure;

public sealed class AdminRepository(IngestionDbContext dbContext) : IAdminRepository
{
    private AdminOperation? _pendingOperation;

    public async Task<AdminOperationReservation> ReserveOperationAsync(
        string appId,
        Guid idempotencyKey,
        string fingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var key = idempotencyKey.ToString("D");
        var existing = await dbContext.AdminOperations.AsNoTracking()
            .SingleOrDefaultAsync(operation => operation.AppId == appId && operation.IdempotencyKey == key, cancellationToken);
        if (existing is not null)
        {
            return ToReservation(existing, fingerprint);
        }

        var operation = new AdminOperation(Guid.NewGuid(), appId, key, fingerprint, AdminOperation.ProcessingState, now);
        dbContext.AdminOperations.Add(operation);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            _pendingOperation = operation;
            return new AdminOperationReservation(AdminReservationDisposition.Proceed, null);
        }
        catch (DbUpdateException exception) when (IsIdempotencyViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            var winner = await dbContext.AdminOperations.AsNoTracking()
                .SingleOrDefaultAsync(winner => winner.AppId == appId && winner.IdempotencyKey == key, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return ToReservation(winner, fingerprint);
        }
    }

    public async Task CompleteReservedOperationAsync(string safeResult, CancellationToken cancellationToken)
    {
        var operation = _pendingOperation
            ?? throw new InvalidOperationException("No admin operation was reserved.");
        operation.Complete(safeResult);
        await dbContext.SaveChangesAsync(cancellationToken);
        _pendingOperation = null;
    }

    public async Task AbandonReservedOperationAsync(CancellationToken cancellationToken)
    {
        if (_pendingOperation is null)
        {
            return;
        }

        var operation = _pendingOperation;
        _pendingOperation = null;
        dbContext.ChangeTracker.Clear();
        dbContext.AdminOperations.Remove(operation);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<ServiceClient?> FindClientByIdAsync(Guid clientId, CancellationToken cancellationToken) =>
        dbContext.ServiceClients.SingleOrDefaultAsync(client => client.Id == clientId, cancellationToken);

    public Task<ServiceClient?> FindClientByNameAsync(string name, CancellationToken cancellationToken) =>
        dbContext.ServiceClients.SingleOrDefaultAsync(client => client.Name == name, cancellationToken);

    public async Task<IReadOnlyList<ClientCredential>> ListCredentialsByClientAsync(Guid clientId, CancellationToken cancellationToken) =>
        await dbContext.ClientCredentials
            .AsNoTracking()
            .Where(credential => credential.ServiceClientId == clientId)
            .OrderBy(credential => credential.CreatedAt)
            .ThenBy(credential => credential.Id)
            .ToListAsync(cancellationToken);

    public Task<ClientCredential?> FindCredentialByIdAsync(Guid credentialId, CancellationToken cancellationToken) =>
        dbContext.ClientCredentials.SingleOrDefaultAsync(credential => credential.Id == credentialId, cancellationToken);

    public async Task<AdminPage<ServiceClient>> ListClientsAsync(int limit, AdminCursorKey? cursor, CancellationToken cancellationToken)
    {
        IQueryable<ServiceClient> query = dbContext.ServiceClients.AsNoTracking();
        if (cursor is { } key)
        {
            query = query.Where(client =>
                client.CreatedAt > key.Timestamp ||
                (client.CreatedAt == key.Timestamp && client.Id.CompareTo(key.Id) > 0));
        }

        var items = await query
            .OrderBy(client => client.CreatedAt)
            .ThenBy(client => client.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        return TrimPage(items, limit);
    }

    public async Task<AdminPage<AdminAuditEventMetadata>> ListAuditAsync(int limit, AdminCursorKey? cursor, CancellationToken cancellationToken)
    {
        IQueryable<AdminAuditEvent> query = dbContext.AdminAuditEvents.AsNoTracking();
        if (cursor is { } key)
        {
            query = query.Where(auditEvent =>
                auditEvent.OccurredAt > key.Timestamp ||
                (auditEvent.OccurredAt == key.Timestamp && auditEvent.Id.CompareTo(key.Id) > 0));
        }

        var items = await query
            .OrderBy(auditEvent => auditEvent.OccurredAt)
            .ThenBy(auditEvent => auditEvent.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var page = TrimPage(items, limit);
        return new AdminPage<AdminAuditEventMetadata>(
            page.Items.Select(ToMetadata).ToList(),
            page.HasMore);
    }

    public void AddClient(ServiceClient client) => dbContext.ServiceClients.Add(client);

    public void AddCredential(ClientCredential credential) => dbContext.ClientCredentials.Add(credential);

    public void AddAuditEvent(
        AdminActor actor,
        string action,
        string outcome,
        DateTimeOffset occurredAt,
        string? targetType,
        string? targetId,
        string? operation,
        string? allowlistedJson) =>
        dbContext.AdminAuditEvents.Add(new AdminAuditEvent(
            Guid.NewGuid(),
            actor,
            action,
            outcome,
            occurredAt,
            targetType,
            targetId,
            operation,
            allowlistedJson));

    public bool IsClientNameViolation(Exception exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
        && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
        && postgresException.ConstraintName == "IX_service_clients_Name";

    public bool IsConcurrencyViolation(Exception exception) => exception is DbUpdateConcurrencyException;

    private static AdminOperationReservation ToReservation(AdminOperation operation, string fingerprint)
    {
        if (!string.Equals(operation.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return new AdminOperationReservation(AdminReservationDisposition.FingerprintMismatch, null);
        }

        return operation.IsCompleted
            ? new AdminOperationReservation(AdminReservationDisposition.Replayed, operation.SafeResult)
            : new AdminOperationReservation(AdminReservationDisposition.InProgress, null);
    }

    private static bool IsIdempotencyViolation(DbUpdateException exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
        && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
        && postgresException.ConstraintName == "IX_admin_operations_AppId_IdempotencyKey";

    private static AdminPage<T> TrimPage<T>(List<T> items, int limit)
    {
        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new AdminPage<T>(items, hasMore);
    }

    private static AdminAuditEventMetadata ToMetadata(AdminAuditEvent auditEvent) =>
        new(
            auditEvent.Id,
            auditEvent.ActorSubject,
            auditEvent.AppId,
            auditEvent.Action,
            auditEvent.Outcome,
            auditEvent.OccurredAt,
            auditEvent.TargetType,
            auditEvent.TargetId,
            auditEvent.Operation,
            auditEvent.AllowlistedJson);
}
