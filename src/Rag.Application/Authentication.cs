using Rag.Domain;

namespace Rag.Application;

public sealed record CredentialSecretHash(byte[] Hash, byte[] Salt, int Version);

public sealed record IssuedCredential(string KeyId, string Secret, Guid CredentialId, Guid ServiceClientId);

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

public sealed record CredentialIdentity(Guid CredentialId, Guid ServiceClientId, int Version);

public sealed class CredentialConcurrencyException(string message, Exception innerException) : Exception(message, innerException);

public interface ICredentialRepository
{
    Task<ClientCredential?> FindByKeyIdAsync(string keyId, CancellationToken cancellationToken);

    Task<ClientCredential?> FindByIdAsync(Guid credentialId, CancellationToken cancellationToken);

    Task<ServiceClient?> FindServiceClientByNameAsync(string name, CancellationToken cancellationToken);

    void Add(ServiceClient serviceClient, ClientCredential credential);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface ICredentialSecretHasher
{
    CredentialSecretHash Hash(string secret);

    bool Verify(string secret, ClientCredential credential);

    void VerifyDummy(string? secret);
}

public interface ICredentialGenerator
{
    string GenerateKeyId();

    string GenerateSecret();
}

public interface IAccessTokenIssuer
{
    AccessToken Issue(ClientCredential credential, DateTimeOffset now);
}

public interface ICredentialStateValidator
{
    Task<bool> IsCurrentAsync(CredentialIdentity identity, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class CredentialExchangeHandler(
    ICredentialRepository repository,
    ICredentialSecretHasher secretHasher,
    IAccessTokenIssuer tokenIssuer)
{
    public async Task<AccessToken?> ExchangeAsync(string? keyId, string? secret, CancellationToken cancellationToken = default)
    {
        if (!ClientCredential.IsValidKeyId(keyId) || string.IsNullOrWhiteSpace(secret) || secret.Length > 512)
        {
            secretHasher.VerifyDummy(secret);
            return null;
        }

        var credential = await repository.FindByKeyIdAsync(keyId!, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (credential is null || !credential.IsActiveAt(now))
        {
            secretHasher.VerifyDummy(secret);
            return null;
        }

        if (!secretHasher.Verify(secret, credential))
        {
            return null;
        }

        return tokenIssuer.Issue(credential, now);
    }
}

public sealed class CredentialOperator(
    ICredentialRepository repository,
    ICredentialGenerator generator,
    ICredentialSecretHasher secretHasher)
{
    public async Task<IssuedCredential> IssueAsync(string serviceClientName, DateTimeOffset? expiresAt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceClientName))
        {
            throw new ArgumentException("A service client name is required.", nameof(serviceClientName));
        }

        if (await repository.FindServiceClientByNameAsync(serviceClientName.Trim(), cancellationToken) is not null)
        {
            throw new InvalidOperationException("The service client name already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var serviceClient = new ServiceClient(Guid.NewGuid(), serviceClientName, now);
        var secret = generator.GenerateSecret();
        var material = secretHasher.Hash(secret);
        var credential = new ClientCredential(
            Guid.NewGuid(), serviceClient.Id, generator.GenerateKeyId(), material.Hash, material.Salt, material.Version, now, expiresAt);
        repository.Add(serviceClient, credential);
        await repository.SaveChangesAsync(cancellationToken);
        return new IssuedCredential(credential.KeyId, secret, credential.Id, serviceClient.Id);
    }

    public async Task<IssuedCredential> RotateAsync(string keyId, CancellationToken cancellationToken = default)
    {
        var credential = await repository.FindByKeyIdAsync(keyId, cancellationToken)
            ?? throw new InvalidOperationException("The credential does not exist.");
        var secret = generator.GenerateSecret();
        var material = secretHasher.Hash(secret);
        credential.Rotate(material.Hash, material.Salt, material.Version, DateTimeOffset.UtcNow);
        await SaveMutationAsync(cancellationToken);
        return new IssuedCredential(credential.KeyId, secret, credential.Id, credential.ServiceClientId);
    }

    public async Task RevokeAsync(string keyId, CancellationToken cancellationToken = default)
    {
        var credential = await repository.FindByKeyIdAsync(keyId, cancellationToken)
            ?? throw new InvalidOperationException("The credential does not exist.");
        credential.Revoke(DateTimeOffset.UtcNow);
        await SaveMutationAsync(cancellationToken);
    }

    private async Task SaveMutationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await repository.SaveChangesAsync(cancellationToken);
        }
        catch (CredentialConcurrencyException exception)
        {
            throw new InvalidOperationException("The credential changed concurrently. Re-read its state before retrying the operation.", exception);
        }
    }
}
