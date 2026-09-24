using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Rag.Application;
using Rag.Application.Auth;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.UnitTests.Auth;

public sealed class TokenScopeExchangeTests
{
    [Fact]
    public async Task Scope_is_optional_and_omitted_scope_preserves_the_legacy_exchange()
    {
        using var fixture = new ExchangeFixture(enabled: false);
        var result = await fixture.Handler.ExchangeScopedAsync(fixture.Credential.KeyId, fixture.Secret, scope: null);

        Assert.Equal(TokenExchangeOutcome.Success, result.Outcome);
        Assert.NotNull(result.Token);
        Assert.Null(result.Token!.Scope);
        var parsed = Parse(result.Token.Value);
        Assert.Null(parsed.Claims.FirstOrDefault(claim => claim.Type == "scope"));
        Assert.Null(parsed.Claims.FirstOrDefault(claim => claim.Type == "collection_id"));
        Assert.Null(parsed.Claims.FirstOrDefault(claim => claim.Type == "collection_grant_version"));
    }

    [Fact]
    public async Task Requested_scope_is_normalized_and_echoed_in_sorted_order()
    {
        using var fixture = new ExchangeFixture(enabled: true);
        var result = await fixture.Handler.ExchangeScopedAsync(
            fixture.Credential.KeyId,
            fixture.Secret,
            "historical:uploads.write historical:operations.read");

        Assert.Equal(TokenExchangeOutcome.Success, result.Outcome);
        Assert.Equal("historical:operations.read historical:uploads.write", result.Token!.Scope);
        Assert.Equal(
            "historical:operations.read historical:uploads.write",
            Parse(result.Token.Value).Claims.Single(claim => claim.Type == "scope").Value);
    }

    [Fact]
    public async Task Unknown_scope_returns_invalid_scope()
    {
        using var fixture = new ExchangeFixture(enabled: true);
        var result = await fixture.Handler.ExchangeScopedAsync(fixture.Credential.KeyId, fixture.Secret, "historical:unknown.read");

        Assert.Equal(TokenExchangeOutcome.InvalidScope, result.Outcome);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task Ungranted_scope_returns_invalid_scope()
    {
        using var fixture = new ExchangeFixture(enabled: true, grantedScopes: [HistoricalScopes.OperationsRead]);
        var result = await fixture.Handler.ExchangeScopedAsync(fixture.Credential.KeyId, fixture.Secret, HistoricalScopes.UploadsWrite);

        Assert.Equal(TokenExchangeOutcome.InvalidScope, result.Outcome);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task Expired_or_revoked_credentials_remain_unauthorized_even_with_a_valid_scope()
    {
        using var fixture = new ExchangeFixture(enabled: true);
        var expired = await fixture.Handler.ExchangeScopedAsync(fixture.ExpiredCredential.KeyId, fixture.Secret, HistoricalScopes.UploadsWrite);
        var revoked = await fixture.Handler.ExchangeScopedAsync(fixture.RevokedCredential.KeyId, fixture.Secret, HistoricalScopes.UploadsWrite);

        Assert.Equal(TokenExchangeOutcome.Unauthorized, expired.Outcome);
        Assert.Null(expired.Token);
        Assert.Equal(TokenExchangeOutcome.Unauthorized, revoked.Outcome);
        Assert.Null(revoked.Token);
    }

    [Fact]
    public async Task Jwt_includes_client_and_credential_identity_scope_and_collection_grant_claims()
    {
        using var fixture = new ExchangeFixture(enabled: true);
        var result = await fixture.Handler.ExchangeScopedAsync(fixture.Credential.KeyId, fixture.Secret, HistoricalScopes.UploadsWrite);

        Assert.Equal(TokenExchangeOutcome.Success, result.Outcome);
        var parsed = Parse(result.Token!.Value);
        Assert.Equal(fixture.Credential.ServiceClientId.ToString("D"), parsed.Claims.Single(claim => claim.Type == "client_id").Value);
        Assert.Equal(fixture.Credential.Id.ToString("D"), parsed.Claims.Single(claim => claim.Type == "credential_id").Value);
        Assert.Equal("1", parsed.Claims.Single(claim => claim.Type == "credential_version").Value);
        Assert.Equal(HistoricalScopes.UploadsWrite, parsed.Claims.Single(claim => claim.Type == "scope").Value);
        Assert.Equal(fixture.LegacyCollectionId.ToString("D"), parsed.Claims.Single(claim => claim.Type == "collection_id").Value);
        Assert.Equal("3", parsed.Claims.Single(claim => claim.Type == "collection_grant_version").Value);
    }

    [Fact]
    public void HistoricalScopes_satisfaction_rejects_a_token_without_the_required_scope()
    {
        Assert.False(HistoricalScopes.Satisfies([HistoricalScopes.OperationsRead], HistoricalScopes.UploadsWrite));
        Assert.False(HistoricalScopes.Satisfies(null, HistoricalScopes.UploadsWrite));
        Assert.True(HistoricalScopes.Satisfies([HistoricalScopes.OperationsRead, HistoricalScopes.UploadsWrite], HistoricalScopes.UploadsWrite));
    }

    [Fact]
    public async Task Feature_gate_disabled_rejects_historical_scopes_even_when_granted()
    {
        using var fixture = new ExchangeFixture(enabled: false);
        var result = await fixture.Handler.ExchangeScopedAsync(fixture.Credential.KeyId, fixture.Secret, HistoricalScopes.UploadsWrite);

        Assert.Equal(TokenExchangeOutcome.InvalidScope, result.Outcome);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task Invalid_credentials_with_a_scope_remain_unauthorized()
    {
        using var fixture = new ExchangeFixture(enabled: true);
        var result = await fixture.Handler.ExchangeScopedAsync("invalid-key", fixture.Secret, HistoricalScopes.UploadsWrite);

        Assert.Equal(TokenExchangeOutcome.Unauthorized, result.Outcome);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task Scoped_exchange_is_invalidated_by_rotation_and_revocation_through_version_and_status_checks()
    {
        var hasher = new Argon2idCredentialSecretHasher();
        var initial = hasher.Hash("initial-secret");
        var serviceClientId = Guid.NewGuid();
        var credential = new ClientCredential(
        Guid.NewGuid(),
        serviceClientId,
        "abcdefghijklmnopqrstuvwxyza",
        initial.Hash,
        initial.Salt,
        initial.Version,
        DateTimeOffset.UtcNow);
        var repository = new MutableCredentialRepository(
        credential,
        serviceClientId,
        new ServiceClientGrant([HistoricalScopes.UploadsWrite, HistoricalScopes.OperationsRead], Guid.NewGuid(), 1));
        var credentialOperator = new CredentialOperator(repository, new FixedGenerator("rotated-secret"), hasher);

        using var rsa = RSA.Create(2048);
        var jwtOptions = CreateJwtOptions(rsa);
        using var keys = new JwtKeyMaterial(jwtOptions);
        var exchange = new CredentialExchangeHandler(
        repository,
        hasher,
        new JwtAccessTokenIssuer(Options.Create(jwtOptions), keys),
        new HistoricalIngestionOptions { Enabled = true });

        var first = await exchange.ExchangeScopedAsync(credential.KeyId, "initial-secret", HistoricalScopes.UploadsWrite);
        Assert.Equal(TokenExchangeOutcome.Success, first.Outcome);
        Assert.Equal("1", Parse(first.Token!.Value).Claims.Single(claim => claim.Type == "credential_version").Value);

        var rotated = await credentialOperator.RotateAsync(credential.KeyId);

        Assert.Equal(2, credential.Version);
        Assert.False(await repository.IsCurrentAsync(
        new CredentialIdentity(credential.Id, credential.ServiceClientId, 1),
        DateTimeOffset.UtcNow,
        CancellationToken.None));

        var second = await exchange.ExchangeScopedAsync(credential.KeyId, rotated.Secret, HistoricalScopes.UploadsWrite);
        Assert.Equal(TokenExchangeOutcome.Success, second.Outcome);
        Assert.Equal("2", Parse(second.Token!.Value).Claims.Single(claim => claim.Type == "credential_version").Value);

        var oldSecret = await exchange.ExchangeScopedAsync(credential.KeyId, "initial-secret", HistoricalScopes.UploadsWrite);
        Assert.Equal(TokenExchangeOutcome.Unauthorized, oldSecret.Outcome);

        await credentialOperator.RevokeAsync(credential.KeyId);
        Assert.False(await repository.IsCurrentAsync(
        new CredentialIdentity(credential.Id, credential.ServiceClientId, 2),
        DateTimeOffset.UtcNow,
        CancellationToken.None));

        var revokedExchange = await exchange.ExchangeScopedAsync(credential.KeyId, rotated.Secret, HistoricalScopes.UploadsWrite);
        Assert.Equal(TokenExchangeOutcome.Unauthorized, revokedExchange.Outcome);
    }

    private static JwtSecurityToken Parse(string token) => new JwtSecurityTokenHandler().ReadJwtToken(token);

    private sealed class ExchangeFixture : IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly JwtKeyMaterial _keyMaterial;

        public ExchangeFixture(bool enabled, string[]? grantedScopes = null)
        {
            var scopes = grantedScopes ?? [HistoricalScopes.UploadsWrite, HistoricalScopes.OperationsRead];
            var hasher = new Argon2idCredentialSecretHasher();
            Secret = "test-secret";
            var material = hasher.Hash(Secret);
            var serviceClientId = Guid.NewGuid();
            Credential = new ClientCredential(Guid.NewGuid(), serviceClientId, "abcdefghijklmnopqrstuvwxyza", material.Hash, material.Salt, material.Version, DateTimeOffset.UtcNow);
            ExpiredCredential = new ClientCredential(
                Guid.NewGuid(), serviceClientId, "abcdefghijklmnopqrstuvwxyzb", material.Hash, material.Salt, material.Version,
                DateTimeOffset.UtcNow.AddMinutes(-30), DateTimeOffset.UtcNow.AddMinutes(-15));
            RevokedCredential = new ClientCredential(Guid.NewGuid(), serviceClientId, "abcdefghijklmnopqrstuvwxyzc", material.Hash, material.Salt, material.Version, DateTimeOffset.UtcNow);
            RevokedCredential.Revoke(DateTimeOffset.UtcNow);
            LegacyCollectionId = Guid.NewGuid();

            var options = CreateJwtOptions(_rsa);
            _keyMaterial = new JwtKeyMaterial(options);
            var repository = new StubCredentialRepository(
                Credential,
                ExpiredCredential,
                RevokedCredential,
                serviceClientId,
                new ServiceClientGrant(scopes, LegacyCollectionId, 3));
            Handler = new CredentialExchangeHandler(
                repository,
                hasher,
                new JwtAccessTokenIssuer(Options.Create(options), _keyMaterial),
                new HistoricalIngestionOptions { Enabled = enabled });
        }

        public CredentialExchangeHandler Handler { get; }

        public ClientCredential Credential { get; }

        public ClientCredential ExpiredCredential { get; }

        public ClientCredential RevokedCredential { get; }

        public string Secret { get; }

        public Guid LegacyCollectionId { get; }

        public void Dispose()
        {
            _keyMaterial.Dispose();
            _rsa.Dispose();
        }
    }

    private static JwtOptions CreateJwtOptions(RSA rsa) => new()
    {
        Issuer = "test-issuer",
        Audience = "test-audience",
        CurrentSigningKey = new JwtPrivateKeyOptions { KeyId = "signing-key-1", PrivateKeyPem = rsa.ExportRSAPrivateKeyPem() },
        ValidationKeys = [new JwtPublicKeyOptions { KeyId = "signing-key-1", PublicKeyPem = rsa.ExportRSAPublicKeyPem() }],
    };

        private sealed class FixedGenerator(string secret) : ICredentialGenerator
        {
            public string GenerateKeyId() => "abcdefghijklmnopqrstuvwxyza";

            public string GenerateSecret() => secret;
        }

        private sealed class MutableCredentialRepository(
            ClientCredential credential,
            Guid grantServiceClientId,
            ServiceClientGrant grant) : ICredentialRepository, ICredentialStateValidator
        {
            public Task<ClientCredential?> FindByKeyIdAsync(string keyId, CancellationToken cancellationToken) =>
                Task.FromResult(credential.KeyId == keyId ? credential : null);

            public Task<ClientCredential?> FindByIdAsync(Guid credentialId, CancellationToken cancellationToken) =>
                Task.FromResult(credential.Id == credentialId ? credential : null);

            public Task<ServiceClient?> FindServiceClientByNameAsync(string name, CancellationToken cancellationToken) =>
                Task.FromResult<ServiceClient?>(null);

            public void Add(ServiceClient serviceClient, ClientCredential newCredential) => throw new NotSupportedException();

            public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<bool> IsCurrentAsync(CredentialIdentity identity, DateTimeOffset now, CancellationToken cancellationToken) =>
                Task.FromResult(
                    identity.CredentialId == credential.Id &&
                    identity.ServiceClientId == credential.ServiceClientId &&
                    identity.Version == credential.Version &&
                    credential.IsActiveAt(now));

            public Task<ServiceClientGrant?> FindGrantAsync(Guid serviceClientId, CancellationToken cancellationToken) =>
                Task.FromResult(serviceClientId == grantServiceClientId ? grant : null);
        }

        private sealed class StubCredentialRepository : ICredentialRepository, ICredentialStateValidator
    {
        private readonly ClientCredential _credential;
        private readonly ClientCredential _expiredCredential;
        private readonly ClientCredential _revokedCredential;
        private readonly Guid _grantServiceClientId;
        private readonly ServiceClientGrant _grant;

        public StubCredentialRepository(
            ClientCredential credential,
            ClientCredential expiredCredential,
            ClientCredential revokedCredential,
            Guid grantServiceClientId,
            ServiceClientGrant grant)
        {
            _credential = credential;
            _expiredCredential = expiredCredential;
            _revokedCredential = revokedCredential;
            _grantServiceClientId = grantServiceClientId;
            _grant = grant;
        }

        public Task<ClientCredential?> FindByKeyIdAsync(string keyId, CancellationToken cancellationToken)
        {
            ClientCredential? credential = keyId switch
            {
                _ when keyId == _credential.KeyId => _credential,
                _ when keyId == _expiredCredential.KeyId => _expiredCredential,
                _ when keyId == _revokedCredential.KeyId => _revokedCredential,
                _ => null,
            };
            return Task.FromResult(credential);
        }

        public Task<ClientCredential?> FindByIdAsync(Guid credentialId, CancellationToken cancellationToken) =>
            Task.FromResult(credentialId == _credential.Id ? _credential : null);

        public Task<ServiceClient?> FindServiceClientByNameAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<ServiceClient?>(null);

        public void Add(ServiceClient serviceClient, ClientCredential newCredential) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> IsCurrentAsync(CredentialIdentity identity, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(identity.CredentialId == _credential.Id &&
                identity.ServiceClientId == _credential.ServiceClientId &&
                identity.Version == _credential.Version &&
                _credential.IsActiveAt(now));

        public Task<ServiceClientGrant?> FindGrantAsync(Guid serviceClientId, CancellationToken cancellationToken) =>
            Task.FromResult(serviceClientId == _grantServiceClientId ? _grant : null);
    }
}
