using Rag.Application;
using Rag.Domain;

namespace Rag.UnitTests;

public sealed class ListAuditHandlerTests
{
    [Fact]
    public async Task List_audit_returns_keyset_pages_and_cursors()
    {
        var first = Audit("2026-01-01T00:00:00+00:00", "00000000-0000-0000-0000-000000000001");
        var second = Audit("2026-01-01T00:00:00+00:00", "00000000-0000-0000-0000-000000000002");
        var third = Audit("2026-01-02T00:00:00+00:00", "00000000-0000-0000-0000-000000000003");
        var repository = new InMemoryAdminRepository([third, first, second]);
        var handler = new ListAuditHandler(repository);

        var firstPage = await handler.HandleAsync(2, null);
        var secondPage = await handler.HandleAsync(2, firstPage.NextCursor);

        Assert.Equal([first.Id, second.Id], firstPage.Items.Select(item => item.Id));
        Assert.Equal(AdminCursor.Encode(second.OccurredAt, second.Id), firstPage.NextCursor);
        Assert.Equal([third.Id], secondPage.Items.Select(item => item.Id));
        Assert.Null(secondPage.NextCursor);
        Assert.Equal(2, repository.LastLimit);
        Assert.Equal(new AdminCursorKey(second.OccurredAt, second.Id), repository.LastCursor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task List_audit_rejects_an_invalid_limit_before_querying(int limit)
    {
        var repository = new InMemoryAdminRepository([]);

        await Assert.ThrowsAsync<ArgumentException>(() => new ListAuditHandler(repository).HandleAsync(limit, null));

        Assert.Equal(0, repository.ListAuditCalls);
    }

    [Fact]
    public async Task List_audit_rejects_an_invalid_cursor_before_querying()
    {
        var repository = new InMemoryAdminRepository([]);

        await Assert.ThrowsAsync<ArgumentException>(() => new ListAuditHandler(repository).HandleAsync(1, "invalid-cursor"));

        Assert.Equal(0, repository.ListAuditCalls);
    }

    private static AdminAuditEventMetadata Audit(string occurredAt, string id) =>
        new(
            Guid.Parse(id),
            "operator",
            "admin-app",
            "create_client",
            "succeeded",
            DateTimeOffset.Parse(occurredAt),
            "client",
            "target",
            "operation",
            null);

    private sealed class InMemoryAdminRepository(IReadOnlyList<AdminAuditEventMetadata> audits) : IAdminRepository
    {
        public int ListAuditCalls { get; private set; }

        public int? LastLimit { get; private set; }

        public AdminCursorKey? LastCursor { get; private set; }

        public Task<AdminPage<AdminAuditEventMetadata>> ListAuditAsync(int limit, AdminCursorKey? cursor, CancellationToken cancellationToken)
        {
            ListAuditCalls++;
            LastLimit = limit;
            LastCursor = cursor;
            var pageItems = audits
                .Where(item => cursor is null || item.OccurredAt > cursor.Timestamp ||
                    (item.OccurredAt == cursor.Timestamp && item.Id.CompareTo(cursor.Id) > 0))
                .OrderBy(item => item.OccurredAt)
                .ThenBy(item => item.Id)
                .Take(limit + 1)
                .ToList();
            var hasMore = pageItems.Count > limit;
            return Task.FromResult<AdminPage<AdminAuditEventMetadata>>(
                new(pageItems.Take(limit).ToList(), hasMore));
        }

        public Task<AdminOperationReservation> ReserveOperationAsync(string appId, Guid idempotencyKey, string fingerprint, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteReservedOperationAsync(string safeResult, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AbandonReservedOperationAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceClient?> FindClientByIdAsync(Guid clientId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceClient?> FindClientByNameAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ClientCredential>> ListCredentialsByClientAsync(Guid clientId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientCredential?> FindCredentialByIdAsync(Guid credentialId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdminPage<ServiceClient>> ListClientsAsync(int limit, AdminCursorKey? cursor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void AddClient(ServiceClient client) => throw new NotSupportedException();
        public void AddCredential(ClientCredential credential) => throw new NotSupportedException();
        public void AddAuditEvent(AdminActor actor, string action, string outcome, DateTimeOffset occurredAt, string? targetType, string? targetId, string? operation, string? allowlistedJson) => throw new NotSupportedException();
        public bool IsClientNameViolation(Exception exception) => throw new NotSupportedException();
        public bool IsConcurrencyViolation(Exception exception) => throw new NotSupportedException();
    }
}
