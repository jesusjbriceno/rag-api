using Rag.Application.Auth;
using Rag.Domain;

namespace Rag.Application;

public sealed record CredentialSecretHash(byte[] Hash, byte[] Salt, int Version);

public sealed record IssuedCredential(string KeyId, string Secret, Guid CredentialId, Guid ServiceClientId);

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt, string? Scope = null);

public sealed record CredentialIdentity(Guid CredentialId, Guid ServiceClientId, int Version);

public sealed record ServiceClientGrant(IReadOnlyList<string> Scopes, Guid? CollectionId, int Version);

public enum TokenExchangeOutcome
{
    Unauthorized,
    InvalidScope,
    Success,
}

public sealed record TokenExchangeResult(TokenExchangeOutcome Outcome, AccessToken? Token = null);

public sealed class HistoricalIngestionOptions
{
    public const string SectionName = "HistoricalIngestion";

    public bool Enabled { get; set; }

    public int MaxNormalizedTextBytes { get; set; } = 10_000_000;

    public int PerClientPendingQuota { get; set; } = 100;

    public long TotalStorageWatermarkBytes { get; set; } = 1_000_000_000;

    public TimeSpan AbandonedUploadExpiry { get; set; } = TimeSpan.FromHours(24);

    public int UploadsRateLimitPermit { get; set; } = 60;

    public TimeSpan UploadsRateLimitWindow { get; set; } = TimeSpan.FromMinutes(1);
}

public sealed class CredentialConcurrencyException(string message, Exception innerException) : Exception(message, innerException);

public interface ICredentialRepository
{
    Task<ClientCredential?> FindByKeyIdAsync(string keyId, CancellationToken cancellationToken);

    Task<ClientCredential?> FindByIdAsync(Guid credentialId, CancellationToken cancellationToken);

    Task<ServiceClient?> FindServiceClientByNameAsync(string name, CancellationToken cancellationToken);

    Task<ServiceClientGrant?> FindGrantAsync(Guid serviceClientId, CancellationToken cancellationToken);

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

    AccessToken Issue(ClientCredential credential, IReadOnlyList<string> scopes, Guid? collectionId, int? collectionGrantVersion, DateTimeOffset now);
}

public interface ICredentialStateValidator
{
    Task<bool> IsCurrentAsync(CredentialIdentity identity, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class CredentialExchangeHandler(
    ICredentialRepository repository,
    ICredentialSecretHasher secretHasher,
    IAccessTokenIssuer tokenIssuer,
    HistoricalIngestionOptions historicalIngestionOptions)
{
    public async Task<AccessToken?> ExchangeAsync(string? keyId, string? secret, CancellationToken cancellationToken = default)
    {
        var result = await ExchangeScopedAsync(keyId, secret, scope: null, cancellationToken);
        return result.Token;
    }

    public async Task<TokenExchangeResult> ExchangeScopedAsync(string? keyId, string? secret, string? scope, CancellationToken cancellationToken = default)
    {
        if (!ClientCredential.IsValidKeyId(keyId) || string.IsNullOrWhiteSpace(secret) || secret.Length > 512)
        {
            secretHasher.VerifyDummy(secret);
            return new TokenExchangeResult(TokenExchangeOutcome.Unauthorized);
        }

        var credential = await repository.FindByKeyIdAsync(keyId!, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (credential is null || !credential.IsActiveAt(now))
        {
            secretHasher.VerifyDummy(secret);
            return new TokenExchangeResult(TokenExchangeOutcome.Unauthorized);
        }

        if (!secretHasher.Verify(secret, credential))
        {
            return new TokenExchangeResult(TokenExchangeOutcome.Unauthorized);
        }

        var requestedScopes = HistoricalScopes.Parse(scope);
        if (requestedScopes.Count == 0)
        {
            return new TokenExchangeResult(TokenExchangeOutcome.Success, tokenIssuer.Issue(credential, now));
        }

        if (requestedScopes.Any(requested => !HistoricalScopes.IsKnown(requested)) || !historicalIngestionOptions.Enabled)
        {
            return new TokenExchangeResult(TokenExchangeOutcome.InvalidScope);
        }

        var grant = await repository.FindGrantAsync(credential.ServiceClientId, cancellationToken);
        if (grant is null || requestedScopes.Any(requested => !grant.Scopes.Contains(requested, StringComparer.Ordinal)))
        {
            return new TokenExchangeResult(TokenExchangeOutcome.InvalidScope);
        }

        var token = tokenIssuer.Issue(credential, requestedScopes, grant.CollectionId, grant.Version, now);
        return new TokenExchangeResult(TokenExchangeOutcome.Success, token);
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
