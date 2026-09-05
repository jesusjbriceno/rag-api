using System.Text.Json;
using Rag.Application;
using Rag.Domain;

namespace Rag.UnitTests;

public sealed class CredentialHandlerTests
{
    [Fact]
    public async Task Issue_credential_persists_hashed_material_and_audits_only_the_key_id()
    {
        var client = Client();
        var repository = new InMemoryAdminRepository();
        repository.Clients.Add(client);

        var result = await new IssueCredentialHandler(repository, new FakeCredentialGenerator(), new FakeSecretHasher()).HandleAsync(
            new AdminActor("operator", "admin-app"), Guid.NewGuid(), "fingerprint", client.Id, " Read-only ", DateTimeOffset.UtcNow.AddDays(1));

        Assert.Equal(FakeCredentialGenerator.Secret, result.Secret);
        Assert.Equal(FakeCredentialGenerator.KeyId, result.Credential.KeyId);
        Assert.Equal("Read-only", result.Credential.Description);
        var credential = Assert.Single(repository.Credentials);
        Assert.Equal(new byte[32], credential.SecretHash);
        Assert.Equal(new byte[16], credential.SecretSalt);
        var metadataJson = JsonSerializer.Serialize(result.Credential);
        Assert.DoesNotContain(FakeCredentialGenerator.Secret, metadataJson);
        var auditJson = Assert.Single(repository.AuditMetadata);
        Assert.Contains(FakeCredentialGenerator.KeyId, auditJson);
        Assert.DoesNotContain(FakeCredentialGenerator.Secret, auditJson);
    }

    [Fact]
    public async Task Issue_credential_rejects_an_expired_expiry_before_persistence()
    {
        var repository = new InMemoryAdminRepository();

        await Assert.ThrowsAsync<ArgumentException>(() => new IssueCredentialHandler(repository, new FakeCredentialGenerator(), new FakeSecretHasher()).HandleAsync(
            new AdminActor("operator", "admin-app"), Guid.NewGuid(), "fingerprint", Guid.NewGuid(), null, DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Equal(0, repository.FindClientCalls);
        Assert.Empty(repository.Credentials);
    }

    [Fact]
    public async Task Issue_credential_rejects_a_missing_client()
    {
        var repository = new InMemoryAdminRepository();

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => new IssueCredentialHandler(repository, new FakeCredentialGenerator(), new FakeSecretHasher()).HandleAsync(
            new AdminActor("operator", "admin-app"), Guid.NewGuid(), "fingerprint", Guid.NewGuid(), null, null));

        Assert.Empty(repository.Credentials);
    }

    [Fact]
    public async Task List_credentials_maps_active_and_expired_credentials_for_an_existing_client()
    {
        var client = Client();
        var repository = new InMemoryAdminRepository();
        repository.Clients.Add(client);
        repository.Credentials.Add(Credential(client.Id, "active credential"));
        repository.Credentials.Add(Credential(client.Id, "expired credential", DateTimeOffset.UtcNow.AddHours(-1)));

        var result = await new ListCredentialsHandler(repository).HandleAsync(client.Id);

        Assert.Equal(["active", "expired"], result.Select(credential => credential.State));
        Assert.Equal(["active credential", "expired credential"], result.Select(credential => credential.Description));
    }

    [Fact]
    public async Task Get_credential_returns_metadata_for_the_requested_credential()
    {
        var client = Client();
        var credential = Credential(client.Id, "Read-only");
        var repository = new InMemoryAdminRepository();
        repository.Credentials.Add(credential);

        var result = await new GetCredentialHandler(repository).HandleAsync(credential.Id);

        Assert.Equal(credential.Id, result.Id);
        Assert.Equal(client.Id, result.ClientId);
        Assert.Equal("Read-only", result.Description);
        Assert.Equal("active", result.State);
    }

    private static ServiceClient Client() => new(Guid.NewGuid(), "reports", DateTimeOffset.UtcNow.AddDays(-1));

    private static ClientCredential Credential(Guid clientId, string description, DateTimeOffset? expiresAt = null) =>
        new(Guid.NewGuid(), clientId, FakeCredentialGenerator.KeyId, new byte[32], new byte[16], 1, DateTimeOffset.UtcNow.AddDays(-2), expiresAt, description);

    private sealed class InMemoryAdminRepository : IAdminRepository
    {
        public List<ServiceClient> Clients { get; } = [];
        public List<ClientCredential> Credentials { get; } = [];
        public List<string?> AuditMetadata { get; } = [];
        public int FindClientCalls { get; private set; }

        public Task<AdminOperationReservation> ReserveOperationAsync(string appId, Guid idempotencyKey, string fingerprint, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(new AdminOperationReservation(AdminReservationDisposition.Proceed, null));

        public Task CompleteReservedOperationAsync(string safeResult, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AbandonReservedOperationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ServiceClient?> FindClientByIdAsync(Guid clientId, CancellationToken cancellationToken)
        {
            FindClientCalls++;
            return Task.FromResult(Clients.SingleOrDefault(client => client.Id == clientId));
        }

        public Task<ServiceClient?> FindClientByNameAsync(string name, CancellationToken cancellationToken) => Task.FromResult<ServiceClient?>(null);

        public Task<IReadOnlyList<ClientCredential>> ListCredentialsByClientAsync(Guid clientId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClientCredential>>(Credentials.Where(credential => credential.ServiceClientId == clientId).ToList());

        public Task<ClientCredential?> FindCredentialByIdAsync(Guid credentialId, CancellationToken cancellationToken) =>
            Task.FromResult(Credentials.SingleOrDefault(credential => credential.Id == credentialId));

        public Task<AdminPage<ServiceClient>> ListClientsAsync(int limit, AdminCursorKey? cursor, CancellationToken cancellationToken) =>
            Task.FromResult(new AdminPage<ServiceClient>([], false));

        public Task<AdminPage<AdminAuditEventMetadata>> ListAuditAsync(int limit, AdminCursorKey? cursor, CancellationToken cancellationToken) =>
            Task.FromResult(new AdminPage<AdminAuditEventMetadata>([], false));

        public void AddClient(ServiceClient client) => Clients.Add(client);

        public void AddCredential(ClientCredential credential) => Credentials.Add(credential);

        public void AddAuditEvent(AdminActor actor, string action, string outcome, DateTimeOffset occurredAt, string? targetType, string? targetId, string? operation, string? allowlistedJson) =>
            AuditMetadata.Add(allowlistedJson);

        public bool IsClientNameViolation(Exception exception) => false;

        public bool IsConcurrencyViolation(Exception exception) => false;
    }

    private sealed class FakeCredentialGenerator : ICredentialGenerator
    {
        public const string KeyId = "abcdefghijklmnopqrstuvwxyz1";
        public const string Secret = "generated-secret";

        public string GenerateKeyId() => KeyId;

        public string GenerateSecret() => Secret;
    }

    private sealed class FakeSecretHasher : ICredentialSecretHasher
    {
        public CredentialSecretHash Hash(string secret) => new(new byte[32], new byte[16], 1);

        public bool Verify(string secret, ClientCredential credential) => false;

        public void VerifyDummy(string? secret)
        {
        }
    }
}
