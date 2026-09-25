using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class AdminAssertionValidatorTests
{
    private const string AppId = "admin-app";
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

        Assert.Null(validator.Validate(CreateAssertion(key, "unknown-kid", now: now), now));
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

        Assert.Null(validator.Validate(CreateAssertion(key, Kid, iat: now, nbf: now, exp: now.AddSeconds(120)), now));
    }

    [Fact]
    public void Assertion_with_the_wrong_issuer_or_audience_is_rejected()
    {
        using var key = RSA.Create(2048);
        var validator = CreateAssertionValidator(key);
        var now = DateTimeOffset.UtcNow;

        Assert.Null(validator.Validate(CreateAssertion(key, Kid, issuer: "other-issuer", now: now), now));
        Assert.Null(validator.Validate(CreateAssertion(key, Kid, audience: "other-audience", now: now), now));
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

        var assertion = validator.Validate(CreateAssertion(key, Kid, now: now), now);

        Assert.NotNull(assertion);
        Assert.Equal(Subject, assertion!.Subject);
        Assert.Equal(AppId, assertion.AppId);
        Assert.Equal(Jti, assertion.Jti);
        Assert.Equal(Issuer, assertion.Issuer);
    }

    [Fact]
    public void Assertion_options_reject_invalid_configuration()
    {
        Assert.Throws<InvalidOperationException>(() => new AdminAssertionOptions().Validate());
        Assert.Throws<InvalidOperationException>(() => new AdminAssertionOptions { Issuer = Issuer, Audience = Audience }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AdminAssertionOptions
        {
            Issuer = Issuer,
            Audience = Audience,
            ValidationKeys = [new AdminAssertionValidationKeyOptions { KeyId = "", PublicKeyPem = "pem" }],
        }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AdminAssertionOptions
        {
            Issuer = Issuer,
            Audience = Audience,
            ValidationKeys =
            [
                new AdminAssertionValidationKeyOptions { KeyId = Kid, PublicKeyPem = "pem-a" },
                new AdminAssertionValidationKeyOptions { KeyId = Kid, PublicKeyPem = "pem-b" },
            ],
        }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AdminAssertionOptions
        {
            Issuer = Issuer,
            Audience = Audience,
            ValidationKeys = [new AdminAssertionValidationKeyOptions { KeyId = Kid, PublicKeyPem = "pem" }],
            LifetimeSeconds = 0,
        }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AdminAssertionOptions
        {
            Issuer = Issuer,
            Audience = Audience,
            ValidationKeys = [new AdminAssertionValidationKeyOptions { KeyId = Kid, PublicKeyPem = "pem" }],
            ClockSkewSeconds = -1,
        }.Validate());
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
        if (subject is not null) claims["sub"] = subject;
        if (appId is not null) claims["app_id"] = appId;
        if (jti is not null) claims["jti"] = jti;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = (iat ?? reference).UtcDateTime,
            NotBefore = (nbf ?? reference).UtcDateTime,
            Expires = (exp ?? reference.AddSeconds(60)).UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = kid }, SecurityAlgorithms.RsaSha256),
        };

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityTokenHandler().CreateToken(descriptor));
    }
}
