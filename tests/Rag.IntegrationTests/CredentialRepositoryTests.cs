using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Rag.Application;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class CredentialRepositoryTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task PostgreSql_persists_credential_secret_material_and_current_version_state()
    {
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;
        await using var context = new IngestionDbContext(options);
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE client_credentials, service_clients, operations, chunks, document_versions, documents, collections CASCADE;");
        var now = DateTimeOffset.UtcNow;
        var client = new ServiceClient(Guid.NewGuid(), "integration-client", now);
        var credential = new ClientCredential(
            Guid.NewGuid(), client.Id, "abcdefghijklmnopqrstuvwxyza", Enumerable.Repeat((byte)1, 32).ToArray(), Enumerable.Repeat((byte)2, 16).ToArray(), 1, now);
        var repository = new CredentialRepository(context);
        repository.Add(client, credential);
        await repository.SaveChangesAsync(CancellationToken.None);
        context.ChangeTracker.Clear();

        var persisted = await repository.FindByKeyIdAsync(credential.KeyId, CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(credential.Id, persisted!.Id);
        Assert.True(await repository.IsCurrentAsync(new CredentialIdentity(credential.Id, client.Id, 1), now, CancellationToken.None));
        persisted.Revoke(now);
        await repository.SaveChangesAsync(CancellationToken.None);
        Assert.False(await repository.IsCurrentAsync(new CredentialIdentity(credential.Id, client.Id, 1), now, CancellationToken.None));
    }

    [Fact]
    public async Task PostgreSql_rejects_a_stale_rotation_after_a_concurrent_revoke()
    {
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;
        await using var setupContext = new IngestionDbContext(options);
        await setupContext.Database.MigrateAsync();
        await setupContext.Database.ExecuteSqlRawAsync("TRUNCATE TABLE client_credentials, service_clients, operations, chunks, document_versions, documents, collections CASCADE;");
        var now = DateTimeOffset.UtcNow;
        var client = new ServiceClient(Guid.NewGuid(), "concurrent-client", now);
        var credential = new ClientCredential(
            Guid.NewGuid(), client.Id, "abcdefghijklmnopqrstuvwxyza", Enumerable.Repeat((byte)1, 32).ToArray(), Enumerable.Repeat((byte)2, 16).ToArray(), 1, now);
        new CredentialRepository(setupContext).Add(client, credential);
        await setupContext.SaveChangesAsync();

        await using var revokeContext = new IngestionDbContext(options);
        await using var rotateContext = new IngestionDbContext(options);
        var revokeRepository = new CredentialRepository(revokeContext);
        var rotateRepository = new CredentialRepository(rotateContext);
        var credentialToRevoke = await revokeRepository.FindByKeyIdAsync(credential.KeyId, CancellationToken.None);
        var staleCredentialToRotate = await rotateRepository.FindByKeyIdAsync(credential.KeyId, CancellationToken.None);
        Assert.NotNull(credentialToRevoke);
        Assert.NotNull(staleCredentialToRotate);

        credentialToRevoke!.Revoke(now.AddMinutes(1));
        await revokeRepository.SaveChangesAsync(CancellationToken.None);
        staleCredentialToRotate!.Rotate(
            Enumerable.Repeat((byte)3, 32).ToArray(),
            Enumerable.Repeat((byte)4, 16).ToArray(),
            1,
            now.AddMinutes(1));

        await Assert.ThrowsAsync<CredentialConcurrencyException>(() => rotateRepository.SaveChangesAsync(CancellationToken.None));
        Assert.Empty(rotateContext.ChangeTracker.Entries());

        await using var verificationContext = new IngestionDbContext(options);
        var verificationRepository = new CredentialRepository(verificationContext);
        var persisted = await verificationRepository.FindByKeyIdAsync(credential.KeyId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(CredentialStatus.Revoked, persisted!.Status);
        Assert.Equal(2, persisted.Version);
        Assert.False(await verificationRepository.IsCurrentAsync(
            new CredentialIdentity(credential.Id, client.Id, 1),
            now.AddMinutes(1),
            CancellationToken.None));
        Assert.False(await verificationRepository.IsCurrentAsync(
            new CredentialIdentity(credential.Id, client.Id, 2),
            now.AddMinutes(1),
            CancellationToken.None));
    }
}
