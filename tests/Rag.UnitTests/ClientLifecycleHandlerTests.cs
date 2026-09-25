using Rag.Application;
using Rag.Domain;

namespace Rag.UnitTests;

public sealed class ClientLifecycleHandlerTests
{
    [Fact]
    public async Task Create_client_persists_audits_and_completes_the_reservation()
    {
        var repository = new InMemoryAdminRepository();
        var idempotencyKey = Guid.NewGuid();

        var result = await new CreateClientHandler(repository).HandleAsync(
            new AdminActor("operator", "admin-app"), idempotencyKey, "fingerprint", "  reports  ", "  Reporting  ");

        Assert.False(result.Replayed);
        Assert.Equal("reports", result.Client.Name);
        Assert.Equal("Reporting", result.Client.Description);
        Assert.Equal(result.Client.Id.ToString("D"), repository.CompletedResult);
        Assert.Single(repository.Clients);
        Assert.Equal(["create_client"], repository.AuditActions);
    }

    [Fact]
    public async Task Create_client_returns_the_existing_client_for_a_replayed_reservation()
    {
        var client = Client("reports");
        var repository = new InMemoryAdminRepository
        {
            Reservation = new AdminOperationReservation(AdminReservationDisposition.Replayed, client.Id.ToString("D")),
        };
        repository.Clients.Add(client);

        var result = await new CreateClientHandler(repository).HandleAsync(
            new AdminActor("operator", "admin-app"), Guid.NewGuid(), "fingerprint", "reports", null);

        Assert.True(result.Replayed);
        Assert.Equal(client.Id, result.Client.Id);
        Assert.Null(repository.CompletedResult);
        Assert.Empty(repository.AuditActions);
    }

    [Fact]
    public async Task List_clients_maps_the_page_and_returns_a_cursor_for_the_last_item()
    {
        var first = Client("first", DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"));
        var last = Client("last", DateTimeOffset.Parse("2026-01-02T00:00:00+00:00"));
        var repository = new InMemoryAdminRepository
        {
            ClientPage = new AdminPage<ServiceClient>([first, last], true),
        };

        var result = await new ListClientsHandler(repository).HandleAsync(2, null);

        Assert.Equal(2, repository.ListLimit);
        Assert.Equal(["first", "last"], result.Items.Select(item => item.Name));
        Assert.Equal(AdminCursor.Encode(last.CreatedAt, last.Id), result.NextCursor);
    }

    [Fact]
    public async Task Get_client_detail_returns_the_client_and_its_credentials()
    {
        var client = Client("reports");
        var repository = new InMemoryAdminRepository();
        repository.Clients.Add(client);
        repository.Credentials.Add(new ClientCredential(
            Guid.NewGuid(), client.Id, "abcdefghijklmnopqrstuvwxyz1", new byte[32], new byte[16], 1,
            client.CreatedAt.AddMinutes(1), description: "Read-only"));

        var result = await new GetClientDetailHandler(repository).HandleAsync(client.Id);

        Assert.Equal(client.Id, result.Client.Id);
        var credential = Assert.Single(result.Credentials);
        Assert.Equal("active", credential.State);
        Assert.Equal("Read-only", credential.Description);
    }

    private static ServiceClient Client(string name, DateTimeOffset? createdAt = null) =>
        new(Guid.NewGuid(), name, createdAt ?? DateTimeOffset.UtcNow);

    private sealed class InMemoryAdminRepository : IAdminRepository
    {
        public List<ServiceClient> Clients { get; } = [];
        public List<ClientCredential> Credentials { get; } = [];
        public List<string> AuditActions { get; } = [];
        public AdminOperationReservation Reservation { get; init; } = new(AdminReservationDisposition.Proceed, null);
        public AdminPage<ServiceClient> ClientPage { get; init; } = new([], false);
        public string? CompletedResult { get; private set; }
        public int? ListLimit { get; private set; }

        public Task<AdminOperationReservation> ReserveOperationAsync(string appId, Guid idempotencyKey, string fingerprint, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(Reservation);
        public Task CompleteReservedOperationAsync(string safeResult, CancellationToken cancellationToken) { CompletedResult = safeResult; return Task.CompletedTask; }
        public Task AbandonReservedOperationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ServiceClient?> FindClientByIdAsync(Guid clientId, CancellationToken cancellationToken) => Task.FromResult(Clients.SingleOrDefault(client => client.Id == clientId));
        public Task<ServiceClient?> FindClientByNameAsync(string name, CancellationToken cancellationToken) => Task.FromResult(Clients.SingleOrDefault(client => client.Name == name));
        public Task<IReadOnlyList<ClientCredential>> ListCredentialsByClientAsync(Guid clientId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ClientCredential>>(Credentials.Where(credential => credential.ServiceClientId == clientId).ToList());
        public Task<ClientCredential?> FindCredentialByIdAsync(Guid credentialId, CancellationToken cancellationToken) => Task.FromResult(Credentials.SingleOrDefault(credential => credential.Id == credentialId));
        public Task<AdminPage<ServiceClient>> ListClientsAsync(int limit, AdminCursorKey? cursor, CancellationToken cancellationToken) { ListLimit = limit; return Task.FromResult(ClientPage); }
        public Task<AdminPage<AdminAuditEventMetadata>> ListAuditAsync(int limit, AdminCursorKey? cursor, CancellationToken cancellationToken) => Task.FromResult(new AdminPage<AdminAuditEventMetadata>([], false));
        public void AddClient(ServiceClient client) => Clients.Add(client);
        public void AddCredential(ClientCredential credential) => Credentials.Add(credential);
        public void AddAuditEvent(AdminActor actor, string action, string outcome, DateTimeOffset occurredAt, string? targetType, string? targetId, string? operation, string? allowlistedJson) => AuditActions.Add(action);
        public bool IsClientNameViolation(Exception exception) => false;
        public bool IsConcurrencyViolation(Exception exception) => false;
    }
}
