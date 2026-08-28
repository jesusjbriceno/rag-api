using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Rag.Application;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class AdminAuthenticatorTests
{
    private const string AppId = "admin-app";
    private const string KeyId = "machine-key-1";
    private const string Secret = "machine-secret";
    private const string Kid = "assertion-key-1";
    private const string Issuer = "admin-issuer";
    private const string Audience = "admin-audience";
    private const string Subject = "cf-subject-123";
    private const string Jti = "jti-1";

    [Fact]
    public async Task Valid_proof_and_assertion_reserve_replay_and_return_the_actor()
    {
        using var key = RSA.Create(2048);
        var authenticator = CreateAuthenticator(key, reserved: true);
        var now = DateTimeOffset.UtcNow;
        var proof = CreateProof(now);

        var actor = await authenticator.AuthenticateAsync(proof, Sign(proof), CreateAssertion(key, now), now);

        Assert.NotNull(actor);
        Assert.Equal(Subject, actor.Value.ActorSubject);
        Assert.Equal(AppId, actor.Value.AppId);
    }

    [Fact]
    public async Task Forged_machine_proof_is_rejected_before_assertion_or_replay()
    {
        using var key = RSA.Create(2048);
        var replay = new RecordingReplayRepository(reserved: true);
        var authenticator = CreateAuthenticator(key, replay);
        var now = DateTimeOffset.UtcNow;
        var proof = CreateProof(now);

        var actor = await authenticator.AuthenticateAsync(proof, Sign(proof, "attacker-secret"), CreateAssertion(key, now), now);

        Assert.Null(actor);
        Assert.Equal(0, replay.ReserveCount);
    }

    [Fact]
    public async Task Invalid_assertion_is_rejected_before_replay()
    {
        using var key = RSA.Create(2048);
        using var forgedKey = RSA.Create(2048);
        var replay = new RecordingReplayRepository(reserved: true);
        var authenticator = CreateAuthenticator(key, replay);
        var now = DateTimeOffset.UtcNow;
        var proof = CreateProof(now);

        var actor = await authenticator.AuthenticateAsync(proof, Sign(proof), CreateAssertion(forgedKey, now), now);

        Assert.Null(actor);
        Assert.Equal(0, replay.ReserveCount);
    }

    [Fact]
    public async Task Assertion_for_a_different_app_is_rejected_before_replay()
    {
        using var key = RSA.Create(2048);
        var replay = new RecordingReplayRepository(reserved: true);
        var authenticator = CreateAuthenticator(key, replay);
        var now = DateTimeOffset.UtcNow;
        var proof = CreateProof(now);

        var actor = await authenticator.AuthenticateAsync(proof, Sign(proof), CreateAssertion(key, now, appId: "other-app"), now);

        Assert.Null(actor);
        Assert.Equal(0, replay.ReserveCount);
    }

    [Fact]
    public async Task Replayed_assertion_is_rejected_and_no_actor_is_created()
    {
        using var key = RSA.Create(2048);
        var replay = new RecordingReplayRepository(reserved: false);
        var authenticator = CreateAuthenticator(key, replay);
        var now = DateTimeOffset.UtcNow;
        var proof = CreateProof(now);

        var actor = await authenticator.AuthenticateAsync(proof, Sign(proof), CreateAssertion(key, now), now);

        Assert.Null(actor);
        Assert.Equal(1, replay.ReserveCount);
    }

    [Fact]
    public void Forbidden_identity_headers_are_rejected_case_insensitively()
    {
        foreach (var header in AdminIdentityHeaderPolicy.ForbiddenHeaders)
        {
            Assert.True(AdminIdentityHeaderPolicy.IsForbidden(header));
            Assert.True(AdminIdentityHeaderPolicy.IsForbidden(header.ToUpperInvariant()));
            Assert.True(AdminIdentityHeaderPolicy.IsForbidden(header.ToLowerInvariant()));
        }
    }

    [Fact]
    public void Benign_headers_are_not_forbidden()
    {
        Assert.False(AdminIdentityHeaderPolicy.IsForbidden("Authorization"));
        Assert.False(AdminIdentityHeaderPolicy.IsForbidden("Content-Type"));
        Assert.False(AdminIdentityHeaderPolicy.IsForbidden("Idempotency-Key"));
        Assert.False(AdminIdentityHeaderPolicy.IsForbidden("Accept"));
    }

    private static AdminAuthenticator CreateAuthenticator(RSA key, bool reserved) =>
        CreateAuthenticator(key, new RecordingReplayRepository(reserved));

    private static AdminAuthenticator CreateAuthenticator(RSA key, RecordingReplayRepository replay)
    {
        var assertionOptions = CreateAssertionOptions(key);
        var appAuthOptions = new AdminAppAuthOptions
        {
            Apps = [new AdminAppOptions { AppId = AppId, KeyId = KeyId, CurrentSecret = Secret }],
        };
        return new AdminAuthenticator(
            new AdminMachineProofVerifier(appAuthOptions),
            new AdminAssertionValidator(assertionOptions, new AdminAssertionKeyRing(assertionOptions)),
            replay);
    }

    private static AdminAssertionOptions CreateAssertionOptions(RSA key) => new()
    {
        Issuer = Issuer,
        Audience = Audience,
        ValidationKeys = [new AdminAssertionValidationKeyOptions { KeyId = Kid, PublicKeyPem = key.ExportRSAPublicKeyPem() }],
    };

    private static AdminMachineProof CreateProof(DateTimeOffset now) => new(
        "POST",
        "/api/v1/admin/clients",
        "5f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f3f",
        "3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f5f3f",
        AppId,
        KeyId,
        now.ToUnixTimeSeconds(),
        "idem-1");

    private static string Sign(AdminMachineProof proof, string secret = Secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(AdminMachineProofVerifier.Canonicalize(proof)));
        return Convert.ToBase64String(digest);
    }

    private static string CreateAssertion(RSA key, DateTimeOffset now, string? appId = AppId)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = Subject,
            ["app_id"] = appId!,
            ["jti"] = Jti,
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddSeconds(60).UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = Kid }, SecurityAlgorithms.RsaSha256),
        };

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityTokenHandler().CreateToken(descriptor));
    }

    private sealed class RecordingReplayRepository(bool reserved) : IAdminAssertionReplayRepository
    {
        public int ReserveCount { get; private set; }

        public Task<bool> ReserveAsync(
            string issuer,
            string jti,
            string appId,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken)
        {
            ReserveCount++;
            return Task.FromResult(reserved);
        }
    }
}
