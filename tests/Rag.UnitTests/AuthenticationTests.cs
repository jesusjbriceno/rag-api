using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Rag.Application;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task Exchange_issues_a_15_minute_token_with_credential_identity_claims()
    {
        var hasher = new Argon2idCredentialSecretHasher();
        var material = hasher.Hash("test-secret");
        var credential = new ClientCredential(Guid.NewGuid(), Guid.NewGuid(), "abcdefghijklmnopqrstuvwxyza", material.Hash, material.Salt, material.Version, DateTimeOffset.UtcNow);
        var repository = new InMemoryCredentialRepository(credential);
        using var rsa = RSA.Create(2048);
        using var keys = new JwtKeyMaterial(CreateJwtOptions(rsa));
        var exchange = new CredentialExchangeHandler(repository, hasher, new JwtAccessTokenIssuer(Options.Create(CreateJwtOptions(rsa)), keys));

        var token = await exchange.ExchangeAsync(credential.KeyId, "test-secret");

        Assert.NotNull(token);
        Assert.InRange(token!.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token.Value);
        Assert.Equal("signing-key-1", parsed.Header.Kid);
        Assert.Equal("RS256", parsed.Header.Alg);
        Assert.Equal(credential.ServiceClientId.ToString("D"), parsed.Claims.Single(claim => claim.Type == "client_id").Value);
        Assert.Equal(credential.Id.ToString("D"), parsed.Claims.Single(claim => claim.Type == "credential_id").Value);
        Assert.Equal("1", parsed.Claims.Single(claim => claim.Type == "credential_version").Value);
    }

    [Fact]
    public async Task Rotation_and_revocation_invalidate_the_previous_credential_version()
    {
        var hasher = new Argon2idCredentialSecretHasher();
        var initial = hasher.Hash("first-secret");
        var credential = new ClientCredential(Guid.NewGuid(), Guid.NewGuid(), "abcdefghijklmnopqrstuvwxyza", initial.Hash, initial.Salt, initial.Version, DateTimeOffset.UtcNow);
        var repository = new InMemoryCredentialRepository(credential);
        var operatorService = new CredentialOperator(repository, new FixedCredentialGenerator("next-secret"), hasher);

        var rotated = await operatorService.RotateAsync(credential.KeyId);

        Assert.Equal(2, credential.Version);
        Assert.False(hasher.Verify("first-secret", credential));
        Assert.True(hasher.Verify(rotated.Secret, credential));
        Assert.False(await repository.IsCurrentAsync(new CredentialIdentity(credential.Id, credential.ServiceClientId, 1), DateTimeOffset.UtcNow, CancellationToken.None));
        await operatorService.RevokeAsync(credential.KeyId);
        Assert.False(await repository.IsCurrentAsync(new CredentialIdentity(credential.Id, credential.ServiceClientId, 2), DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public void Argon2id_hashes_are_salted_and_verify_in_constant_time_path()
    {
        var hasher = new Argon2idCredentialSecretHasher();
        var first = hasher.Hash("secret");
        var second = hasher.Hash("secret");
        var credential = new ClientCredential(Guid.NewGuid(), Guid.NewGuid(), "abcdefghijklmnopqrstuvwxyza", first.Hash, first.Salt, first.Version, DateTimeOffset.UtcNow);

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
        Assert.True(hasher.Verify("secret", credential));
        Assert.False(hasher.Verify("incorrect", credential));
    }

    [Fact]
    public void Jwt_configuration_rejects_missing_private_or_validation_key_material()
    {
        var options = new JwtOptions { Issuer = "issuer", Audience = "audience" };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("signing key", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Jwt_key_material_rejects_a_public_only_pem_for_the_current_signing_key()
    {
        using var rsa = RSA.Create(2048);
        var options = CreateJwtOptions(rsa);
        options.CurrentSigningKey.PrivateKeyPem = rsa.ExportRSAPublicKeyPem();

        Assert.Throws<InvalidOperationException>(() => new JwtKeyMaterial(options));
    }

    [Fact]
    public async Task Exchange_failure_paths_perform_one_dummy_verification()
    {
        var cases = new (string? KeyId, string? Secret, ClientCredential? Credential)[]
        {
            (null, null, null),
            ("invalid", "secret", null),
            ("abcdefghijklmnopqrstuvwxyza", null, null),
            ("abcdefghijklmnopqrstuvwxyza", new string('a', 513), null),
            ("abcdefghijklmnopqrstuvwxyza", "secret", null),
            ("abcdefghijklmnopqrstuvwxyza", "secret", CreateRevokedCredential()),
        };

        foreach (var testCase in cases)
        {
            var hasher = new RecordingHasher();
            var exchange = new CredentialExchangeHandler(
                new InMemoryCredentialRepository(testCase.Credential),
                hasher,
                new ThrowingTokenIssuer());

            var token = await exchange.ExchangeAsync(testCase.KeyId, testCase.Secret);

            Assert.Null(token);
            Assert.Equal(1, hasher.DummyVerifications);
            Assert.Equal(0, hasher.Verifications);
        }
    }

    [Fact]
    public async Task Exchange_with_an_active_credential_and_wrong_secret_uses_the_real_verification_path()
    {
        var hasher = new RecordingHasher();
        var credential = new ClientCredential(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "abcdefghijklmnopqrstuvwxyza",
            Enumerable.Repeat((byte)1, 32).ToArray(),
            Enumerable.Repeat((byte)2, 16).ToArray(),
            1,
            DateTimeOffset.UtcNow);
        var exchange = new CredentialExchangeHandler(
            new InMemoryCredentialRepository(credential),
            hasher,
            new ThrowingTokenIssuer());

        var token = await exchange.ExchangeAsync(credential.KeyId, "wrong-secret");

        Assert.Null(token);
        Assert.Equal(0, hasher.DummyVerifications);
        Assert.Equal(1, hasher.Verifications);
    }

    [Fact]
    public async Task Rotation_surfaces_a_concurrent_persistence_conflict_as_a_safe_retry_instruction()
    {
        var hasher = new Argon2idCredentialSecretHasher();
        var material = hasher.Hash("first-secret");
        var credential = new ClientCredential(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "abcdefghijklmnopqrstuvwxyza",
            material.Hash,
            material.Salt,
            material.Version,
            DateTimeOffset.UtcNow);
        var operatorService = new CredentialOperator(
            new InMemoryCredentialRepository(credential, failSaves: true),
            new FixedCredentialGenerator("next-secret"),
            hasher);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => operatorService.RotateAsync(credential.KeyId));

        Assert.Contains("Re-read", exception.Message, StringComparison.Ordinal);
    }

    private static JwtOptions CreateJwtOptions(RSA rsa) => new()
    {
        Issuer = "test-issuer",
        Audience = "test-audience",
        CurrentSigningKey = new JwtPrivateKeyOptions { KeyId = "signing-key-1", PrivateKeyPem = rsa.ExportRSAPrivateKeyPem() },
        ValidationKeys = [new JwtPublicKeyOptions { KeyId = "signing-key-1", PublicKeyPem = rsa.ExportRSAPublicKeyPem() }],
    };

    private sealed class FixedCredentialGenerator(string secret) : ICredentialGenerator
    {
        public string GenerateKeyId() => "abcdefghijklmnopqrstuvwxyza";

        public string GenerateSecret() => secret;
    }

    private static ClientCredential CreateRevokedCredential()
    {
        var credential = new ClientCredential(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "abcdefghijklmnopqrstuvwxyza",
            Enumerable.Repeat((byte)1, 32).ToArray(),
            Enumerable.Repeat((byte)2, 16).ToArray(),
            1,
            DateTimeOffset.UtcNow);
        credential.Revoke(DateTimeOffset.UtcNow);
        return credential;
    }

    private sealed class InMemoryCredentialRepository(ClientCredential? credential, bool failSaves = false) : ICredentialRepository, ICredentialStateValidator
    {
        public Task<ClientCredential?> FindByKeyIdAsync(string keyId, CancellationToken cancellationToken) =>
            Task.FromResult(credential?.KeyId == keyId ? credential : null);

        public Task<ClientCredential?> FindByIdAsync(Guid credentialId, CancellationToken cancellationToken) =>
            Task.FromResult(credential?.Id == credentialId ? credential : null);

        public Task<ServiceClient?> FindServiceClientByNameAsync(string name, CancellationToken cancellationToken) => Task.FromResult<ServiceClient?>(null);

        public void Add(ServiceClient serviceClient, ClientCredential newCredential) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            if (failSaves)
            {
                throw new CredentialConcurrencyException("conflict", new InvalidOperationException());
            }

            return Task.CompletedTask;
        }

        public Task<bool> IsCurrentAsync(CredentialIdentity identity, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(credential is not null && credential.Id == identity.CredentialId && credential.ServiceClientId == identity.ServiceClientId &&
                credential.Version == identity.Version && credential.IsActiveAt(now));
    }

    private sealed class RecordingHasher : ICredentialSecretHasher
    {
        public int DummyVerifications { get; private set; }

        public int Verifications { get; private set; }

        public CredentialSecretHash Hash(string secret) => throw new NotSupportedException();

        public bool Verify(string secret, ClientCredential credential)
        {
            Verifications++;
            return false;
        }

        public void VerifyDummy(string? secret) => DummyVerifications++;
    }

    private sealed class ThrowingTokenIssuer : IAccessTokenIssuer
    {
        public AccessToken Issue(ClientCredential credential, DateTimeOffset now) => throw new InvalidOperationException();
    }
}
