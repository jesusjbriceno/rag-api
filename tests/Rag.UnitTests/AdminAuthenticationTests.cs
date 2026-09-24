using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Rag.Application;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class AdminAuthenticationTests
{
    private const string AppId = "admin-app";
    private const string KeyId = "admin-key";
    private const string Secret = "test-machine-secret";
    private const string Kid = "assertion-key-1";
    private const string Issuer = "admin-issuer";
    private const string Audience = "admin-audience";
    private const string Subject = "cf-subject-123";
    private const string Jti = "jti-1";

    [Fact]
    public void Forged_assertion_signed_with_a_different_key_is_rejected()
    {
        using var ringKey = RSA.Create(2048);
        using var forgedKey = RSA.Create(2048);
        var validator = CreateAssertionValidator(ringKey);
        var jws = CreateAssertion(forgedKey, Kid, now: DateTimeOffset.UtcNow);

        Assert.Null(validator.Validate(jws, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Expired_assertion_is_rejected()
    {
        using var key = RSA.Create(2048);
        var validator = CreateAssertionValidator(key);
        var now = DateTimeOffset.UtcNow;
        var jws = CreateAssertion(key, Kid, iat: now.AddSeconds(-120), nbf: now.AddSeconds(-120), exp: now.AddSeconds(-60));

        Assert.Null(validator.Validate(jws, now));
    }

    [Fact]
    public void Assertion_used_before_its_not_before_time_is_rejected()
    {
        using var key = RSA.Create(2048);
        var validator = CreateAssertionValidator(key);
        var now = DateTimeOffset.UtcNow;
        var jws = CreateAssertion(key, Kid, iat: now, nbf: now.AddSeconds(40), exp: now.AddSeconds(60));

        Assert.Null(validator.Validate(jws, now));
    }

    [Fact]
    public void Assertion_with_an_unknown_key_id_is_rejected()
    {
        using var key = RSA.Create(2048);
        var validator = CreateAssertionValidator(key);
        var now = DateTimeOffset.UtcNow;
        var jws = CreateAssertion(key, "unknown-kid", now: now);

        Assert.Null(validator.Validate(jws, now));
    }

    [Fact]
    public void Assertion_missing_subject_app_or_jti_is_rejected()
    {
        using var key = RSA.Create(2048);
        var validator = CreateAssertionValidator(key);
        var now = DateTimeOffset.UtcNow;

        Assert.Null(validator.Validate(CreateAssertion(key, Kid, subject: null, now: now), now));
        Assert.Null(validator.Validate(CreateAssertion(key, Kid, appId: null, now: now), now));
        Assert.Null(validator.Validate(CreateAssertion(key, Kid, jti: null, now: now), now));
    }

    [Fact]
    public void Assertion_with_a_lifetime_longer_than_the_allowed_window_is_rejected()
    {
        using var key = RSA.Create(2048);
        var validator = CreateAssertionValidator(key);
        var now = DateTimeOffset.UtcNow;
        var jws = CreateAssertion(key, Kid, iat: now, nbf: now, exp: now.AddSeconds(120));

        Assert.Null(validator.Validate(jws, now));
    }

    [Fact]
    public void Emergency_key_removal_revokes_assertions_signed_with_that_key()
    {
        using var key = RSA.Create(2048);
        var options = CreateAssertionOptions(key);
        using var keyRing = new AdminAssertionKeyRing(options);
        var validator = new AdminAssertionValidator(options, keyRing);
        var now = DateTimeOffset.UtcNow;
        var jws = CreateAssertion(key, Kid, now: now);
        Assert.NotNull(validator.Validate(jws, now));

        keyRing.Remove(Kid);

        Assert.Null(validator.Validate(jws, now));
    }

    [Fact]
    public void Valid_assertion_returns_its_subject_app_and_replay_identity()
    {
        using var key = RSA.Create(2048);
        var validator = CreateAssertionValidator(key);
        var now = DateTimeOffset.UtcNow;
        var jws = CreateAssertion(key, Kid, now: now);

        var assertion = validator.Validate(jws, now);

        Assert.NotNull(assertion);
        Assert.Equal(Subject, assertion!.Subject);
        Assert.Equal(AppId, assertion.AppId);
        Assert.Equal(Jti, assertion.Jti);
        Assert.Equal(Issuer, assertion.Issuer);
    }

    [Fact]
    public void Machine_proof_with_a_forged_signature_is_rejected()
    {
        using var key = RSA.Create(2048);
        var verifier = CreateMachineProofVerifier();
        var now = DateTimeOffset.UtcNow;
        var assertion = CreateAssertion(key, Kid, now: now);
        var proof = CreateProof(assertion, now, signature: "deadbeef");

        Assert.False(verifier.Verify(proof, "POST", "/api/v1/admin/clients", Encoding.UTF8.GetBytes("{}"), assertion, now));
    }

    [Fact]
    public void Machine_proof_with_a_stale_timestamp_is_rejected()
    {
        using var key = RSA.Create(2048);
        var verifier = CreateMachineProofVerifier();
        var now = DateTimeOffset.UtcNow;
        var assertion = CreateAssertion(key, Kid, now: now);
        var stale = now.AddSeconds(-120).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var proof = CreateProof(assertion, now, timestamp: stale);

        Assert.False(verifier.Verify(proof, "POST", "/api/v1/admin/clients", Encoding.UTF8.GetBytes("{}"), assertion, now));
    }

    [Fact]
    public void Machine_proof_signed_with_the_previous_secret_is_accepted_during_rotation()
    {
        using var key = RSA.Create(2048);
        var options = new AdminAppAuthOptions
        {
            Apps = [new AdminAppOptions { AppId = AppId, KeyId = KeyId, Secret = Secret, PreviousSecret = "old-secret" }],
        };
        var verifier = new AdminMachineProofVerifier(options);
        var now = DateTimeOffset.UtcNow;
        var assertion = CreateAssertion(key, Kid, now: now);
        var proof = CreateProof(assertion, now, secret: "old-secret");

        Assert.True(verifier.Verify(proof, "POST", "/api/v1/admin/clients", Encoding.UTF8.GetBytes("{}"), assertion, now));
    }

    [Fact]
    public void Machine_proof_for_an_unknown_app_or_key_is_rejected()
    {
        using var key = RSA.Create(2048);
        var verifier = CreateMachineProofVerifier();
        var now = DateTimeOffset.UtcNow;
        var assertion = CreateAssertion(key, Kid, now: now);
        var unknownApp = CreateProof(assertion, now, appId: "other-app");
        var unknownKey = CreateProof(assertion, now, keyId: "other-key");

        Assert.False(verifier.Verify(unknownApp, "POST", "/api/v1/admin/clients", Encoding.UTF8.GetBytes("{}"), assertion, now));
        Assert.False(verifier.Verify(unknownKey, "POST", "/api/v1/admin/clients", Encoding.UTF8.GetBytes("{}"), assertion, now));
    }

    [Fact]
    public void Canonicalization_is_deterministic_and_orders_all_request_parts()
    {
        var body = Encoding.UTF8.GetBytes("{\"name\":\"x\"}");
        var first = AdminMachineProofVerifier.Canonicalize("post", "/api/v1/admin/clients?limit=10", body, "assertion", AppId, KeyId, "1700000000", "idem-1");
        var second = AdminMachineProofVerifier.Canonicalize("POST", "/api/v1/admin/clients?limit=10", body, "assertion", AppId, KeyId, "1700000000", "idem-1");

        Assert.Equal(first, second);
        Assert.StartsWith("POST\n/api/v1/admin/clients?limit=10\n", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticator_builds_an_actor_only_after_all_checks_and_reserves_the_assertion()
    {
        using var key = RSA.Create(2048);
        var authenticator = CreateAuthenticator(key, out var replays);
        var now = DateTimeOffset.UtcNow;
        var assertion = CreateAssertion(key, Kid, now: now);
        var proof = CreateProof(assertion, now);

        var actor = await authenticator.AuthenticateAsync(proof, assertion, "POST", "/api/v1/admin/clients", Encoding.UTF8.GetBytes("{}"), now, CancellationToken.None);

        Assert.NotNull(actor);
        Assert.Equal(Subject, actor!.Value.ActorSubject);
        Assert.Equal(AppId, actor.Value.AppId);
        Assert.Equal([(Issuer, Jti)], replays.Reservations);
    }

    [Fact]
    public async Task Authenticator_rejects_a_replayed_assertion()
    {
        using var key = RSA.Create(2048);
        var authenticator = CreateAuthenticator(key, out var replays, replayed: true);
        var now = DateTimeOffset.UtcNow;
        var assertion = CreateAssertion(key, Kid, now: now);
        var proof = CreateProof(assertion, now);

        var actor = await authenticator.AuthenticateAsync(proof, assertion, "POST", "/api/v1/admin/clients", Encoding.UTF8.GetBytes("{}"), now, CancellationToken.None);

        Assert.Null(actor);
        Assert.Empty(replays.Reservations);
    }

    [Fact]
    public async Task Authenticator_rejects_when_the_machine_app_does_not_match_the_assertion_app()
    {
        using var key = RSA.Create(2048);
        var authenticator = CreateAuthenticator(key, out var replays);
        var now = DateTimeOffset.UtcNow;
        var assertion = CreateAssertion(key, Kid, now: now);
        var proof = CreateProof(assertion, now, appId: "other-app");

        var actor = await authenticator.AuthenticateAsync(proof, assertion, "POST", "/api/v1/admin/clients", Encoding.UTF8.GetBytes("{}"), now, CancellationToken.None);

        Assert.Null(actor);
        Assert.Empty(replays.Reservations);
    }

    [Theory]
    [InlineData("cf-access-jwt-assertion")]
    [InlineData("Cf-Access-Authenticated-User-Email")]
    [InlineData("cf-access-authenticated-user-id")]
    [InlineData("x-forwarded-user")]
    [InlineData("X-Forwarded-Email")]
    [InlineData("x-auth-request-user")]
    public void Proxy_and_cloudflare_identity_headers_are_never_trusted(string headerName) =>
        Assert.True(AdminIdentityHeaderPolicy.IsForbiddenIdentityHeader(headerName));

    [Theory]
    [InlineData("authorization")]
    [InlineData("x-admin-assertion")]
    [InlineData("content-type")]
    public void Ordinary_headers_are_not_flagged_as_forbidden_identity_headers(string headerName) =>
        Assert.False(AdminIdentityHeaderPolicy.IsForbiddenIdentityHeader(headerName));

    [Fact]
    public void Assertion_and_app_auth_options_reject_missing_configuration()
    {
        Assert.Throws<InvalidOperationException>(() => new AdminAssertionOptions().Validate());
        Assert.Throws<InvalidOperationException>(() => new AdminAppAuthOptions().Validate());
    }

    [Fact]
    public void AddInfrastructure_fails_startup_when_admin_plane_is_enabled_but_configuration_is_missing()
    {
        var configuration = new ConfigurationManager();
        configuration["ConnectionStrings:Rag"] = "Host=localhost;Database=rag;Username=rag;Password=rag";
        configuration["AdminPlane:Enabled"] = "true";
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AdminAssertionOptions>>().Value);
    }

    [Fact]
    public void AddInfrastructure_does_not_require_admin_configuration_when_admin_plane_is_disabled()
    {
        var configuration = new ConfigurationManager();
        configuration["ConnectionStrings:Rag"] = "Host=localhost;Database=rag;Username=rag;Password=rag";
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<AdminAuthenticator>());
        Assert.Null(provider.GetService<IAdminAssertionReplayRepository>());
    }

    private static AdminAssertionValidator CreateAssertionValidator(RSA key)
    {
        var options = CreateAssertionOptions(key);
        return new AdminAssertionValidator(options, new AdminAssertionKeyRing(options));
    }

    private static AdminAssertionOptions CreateAssertionOptions(RSA key) => new()
    {
        Issuer = Issuer,
        Audience = Audience,
        ValidationKeys = [new AdminAssertionValidationKeyOptions { KeyId = Kid, PublicKeyPem = key.ExportRSAPublicKeyPem() }],
    };

    private static AdminMachineProofVerifier CreateMachineProofVerifier() => new(new AdminAppAuthOptions
    {
        Apps = [new AdminAppOptions { AppId = AppId, KeyId = KeyId, Secret = Secret }],
    });

    private static AdminAuthenticator CreateAuthenticator(RSA key, out RecordingReplayRepository replays, bool replayed = false)
    {
        replays = new RecordingReplayRepository(replayed);
        var options = CreateAssertionOptions(key);
        var keyRing = new AdminAssertionKeyRing(options);
        return new AdminAuthenticator(
            new AdminAssertionValidator(options, keyRing),
            CreateMachineProofVerifier(),
            replays);
    }

    private static AdminMachineProof CreateProof(
        string assertion,
        DateTimeOffset now,
        string? appId = null,
        string? keyId = null,
        string? secret = null,
        string? timestamp = null,
        string? signature = null)
    {
        var proofAppId = appId ?? AppId;
        var proofKeyId = keyId ?? KeyId;
        var proofTimestamp = timestamp ?? now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var canonical = AdminMachineProofVerifier.Canonicalize(
            "POST", "/api/v1/admin/clients", Encoding.UTF8.GetBytes("{}"), assertion, proofAppId, proofKeyId, proofTimestamp, null);
        return new AdminMachineProof(proofAppId, proofKeyId, proofTimestamp, null, signature ?? SignProof(secret ?? Secret, canonical));
    }

    private static string SignProof(string secret, string canonical)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string CreateAssertion(
        RSA key,
        string kid,
        string? subject = Subject,
        string? appId = AppId,
        string? jti = Jti,
        string? issuer = Issuer,
        string? audience = Audience,
        DateTimeOffset? iat = null,
        DateTimeOffset? nbf = null,
        DateTimeOffset? exp = null,
        DateTimeOffset? now = null)
    {
        var reference = now ?? DateTimeOffset.UtcNow;
        var claims = new Dictionary<string, object>();
        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        if (appId is not null)
        {
            claims["app_id"] = appId;
        }

        if (jti is not null)
        {
            claims["jti"] = jti;
        }

        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = (iat ?? reference).UtcDateTime,
            NotBefore = (nbf ?? reference).UtcDateTime,
            Expires = (exp ?? reference.AddSeconds(60)).UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = kid }, SecurityAlgorithms.RsaSha256),
        });
        return handler.WriteToken(token);
    }

    private sealed class RecordingReplayRepository(bool replayed) : IAdminAssertionReplayRepository
    {
        public List<(string Issuer, string Jti)> Reservations { get; } = [];

        public Task<bool> TryReserveAsync(string issuer, string jti, string appId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        {
            if (!replayed)
            {
                Reservations.Add((issuer, jti));
            }

            return Task.FromResult(!replayed);
        }
    }
}
